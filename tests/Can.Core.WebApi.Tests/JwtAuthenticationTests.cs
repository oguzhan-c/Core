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
    private const string CookieName = "can_access_token";

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

    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync(bool allowHeader = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddCanSecurity(o => o.Jwt = Jwt);
        builder.Services.AddCanWebApi();
        builder.Services.AddCanJwtAuthentication(o => o.AllowAuthorizationHeader = allowHeader);
        builder.Services.AddAuthorization();

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.UseAuthentication();
        app.UseCanTenantResolution();
        app.UseAuthorization();

        app.MapPost("/auth/login", (ITokenService tokens, IAuthCookieService cookies, HttpResponse response) =>
        {
            cookies.SetTokens(response, tokens.CreateAccessToken(new TokenSubject("u-1")), tokens.CreateRefreshToken());
            return Results.NoContent();
        });

        app.MapPost("/auth/logout", (IAuthCookieService cookies, HttpResponse response) =>
        {
            cookies.Clear(response);
            return Results.NoContent();
        });

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

    private static string CreateToken(WebApplication app, TokenSubject subject) =>
        app.Services.GetRequiredService<ITokenService>().CreateAccessToken(subject).Token;

    private static HttpRequestMessage WithCookie(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{CookieName}={token}");
        return request;
    }

    private static HttpRequestMessage WithHeader(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Token_in_cookie_authenticates_and_exposes_claims()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            Guid tenant = Guid.NewGuid();
            string token = CreateToken(app, new TokenSubject("u-1", "ada", "ada@test.local", Roles: ["Editor", "Admin"], TenantId: tenant.ToString()));

            HttpResponseMessage response = await client.SendAsync(WithCookie("/me", token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            JsonElement me = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("u-1", me.GetProperty("id").GetString());
            Assert.Equal("ada@test.local", me.GetProperty("email").GetString());
            Assert.Equal(new[] { "Admin", "Editor" }, me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
            Assert.Equal(tenant.ToString(), me.GetProperty("tenant").GetString());

            // role claim'i ASP.NET yetkilendirmesiyle de çalışır
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithCookie("/admin", token))).StatusCode);
        }
    }

    [Fact]
    public async Task Authorization_header_is_ignored_by_default()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            string token = CreateToken(app, new TokenSubject("u-1"));

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithHeader("/me", token))).StatusCode);
        }
    }

    [Fact]
    public async Task Authorization_header_can_be_enabled_for_non_browser_clients()
    {
        (WebApplication app, HttpClient client) = await CreateAsync(allowHeader: true);
        await using (app)
        {
            string token = CreateToken(app, new TokenSubject("u-1"));

            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithHeader("/me", token))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithCookie("/me", token))).StatusCode);
        }
    }

    [Fact]
    public async Task Missing_invalid_or_expired_tokens_are_rejected()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithCookie("/me", "gecersiz.token.degeri"))).StatusCode);

            var oldTokens = new TokenService(Jwt, new PastClock(DateTimeOffset.UtcNow.AddHours(-2)));
            string expired = oldTokens.CreateAccessToken(new TokenSubject("u-1")).Token;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithCookie("/me", expired))).StatusCode);
        }
    }

    [Fact]
    public async Task User_without_role_is_forbidden()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            string token = CreateToken(app, new TokenSubject("u-2"));

            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithCookie("/admin", token))).StatusCode);
        }
    }

    [Fact]
    public async Task Login_writes_secure_http_only_cookies_and_logout_clears_them()
    {
        (WebApplication app, HttpClient client) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage login = await client.PostAsync("/auth/login", content: null);
            string[] cookies = login.Headers.GetValues("Set-Cookie").ToArray();

            string access = Assert.Single(cookies, c => c.StartsWith("can_access_token=", StringComparison.Ordinal));
            Assert.Contains("httponly", access, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", access, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", access, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/", access, StringComparison.OrdinalIgnoreCase);

            // refresh token yalnızca yenileme endpoint'ine gönderilir
            string refresh = Assert.Single(cookies, c => c.StartsWith("can_refresh_token=", StringComparison.Ordinal));
            Assert.Contains("path=/auth/refresh", refresh, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", refresh, StringComparison.OrdinalIgnoreCase);

            HttpResponseMessage logout = await client.PostAsync("/auth/logout", content: null);
            Assert.All(logout.Headers.GetValues("Set-Cookie"), c => Assert.Contains("expires=Thu, 01 Jan 1970", c, StringComparison.OrdinalIgnoreCase));
        }
    }
}
