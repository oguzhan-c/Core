using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Can.Core.Application;
using Can.Core.Security.DependencyInjection;
using Can.Core.Security.Tokens;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Tests;

public class JwtAuthenticationTests
{
    private static readonly JwtOptions Jwt = new()
    {
        Issuer = "can-tests",
        Audience = "can-clients",
        SigningKey = "jwt-test-anahtari-en-az-otuz-iki-karakter!!",
    };

    private sealed class PastClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddCanSecurity(o => o.Jwt = Jwt);
        builder.Services.AddCanWebApi();
        builder.Services.AddCanJwtAuthentication();
        builder.Services.AddAuthorization();

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.UseAuthentication();
        app.UseCanTenantResolution();
        app.UseAuthorization();

        app.MapGet("/me", (ICurrentUser user, ICurrentTenant tenant) => new
            {
                user.Id,
                user.Email,
                Roles = user.Roles.OrderBy(r => r).ToArray(),
                Tenant = tenant.Id?.ToString(),
            })
            .RequireAuthorization();

        app.MapGet("/admin", () => "ok").RequireAuthorization(policy => policy.RequireRole("Admin"));

        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage WithToken(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Token_from_TokenService_authenticates_and_exposes_claims()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            var tokens = app.Services.GetRequiredService<ITokenService>();
            Guid tenant = Guid.NewGuid();
            string token = tokens.CreateAccessToken(
                new TokenSubject("u-1", "ada", "ada@test.local", Roles: ["Editor", "Admin"], TenantIds: [tenant.ToString()])
            ).Token;

            HttpResponseMessage response = await client.SendAsync(WithToken("/me", token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            JsonElement me = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("u-1", me.GetProperty("id").GetString());
            Assert.Equal("ada@test.local", me.GetProperty("email").GetString());
            Assert.Equal(new[] { "Admin", "Editor" }, me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
            Assert.Equal(tenant.ToString(), me.GetProperty("tenant").GetString());

            // role claim'i ASP.NET yetkilendirmesiyle de çalışır
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken("/admin", token))).StatusCode);
        }
    }

    [Fact]
    public async Task Missing_invalid_or_expired_tokens_are_rejected()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithToken("/me", "gecersiz.token.degeri"))).StatusCode);

            var oldTokens = new TokenService(Jwt, new PastClock(DateTimeOffset.UtcNow.AddHours(-2)));
            string expired = oldTokens.CreateAccessToken(new TokenSubject("u-1")).Token;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithToken("/me", expired))).StatusCode);
        }
    }

    [Fact]
    public async Task User_without_role_is_forbidden()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            string token = app.Services.GetRequiredService<ITokenService>().CreateAccessToken(new TokenSubject("u-2")).Token;

            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken("/admin", token))).StatusCode);
        }
    }
}
