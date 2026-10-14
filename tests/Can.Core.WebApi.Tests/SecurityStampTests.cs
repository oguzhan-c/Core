using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Can.Core.Security.Tokens;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Tests;

public class SecurityStampTests
{
    /// <summary>Kullanıcı "42"nin güncel damgası "YENI".</summary>
    private sealed class FixedValidator : ISecurityStampValidator
    {
        public int Calls { get; private set; }

        public ValueTask<bool> IsCurrentAsync(string userId, string securityStamp, CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(userId == "42" && securityStamp == "YENI");
        }

        public void Invalidate(string userId) { }
    }

    private static async Task<(WebApplication App, HttpClient Client, FixedValidator Validator)> CreateAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        var validator = new FixedValidator();
        builder.Services.AddSingleton<ISecurityStampValidator>(validator);
        builder.Services.AddSingleton<IAuthCookieService>(new AuthCookieService(new AuthCookieOptions()));

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.Use(async (context, next) =>
        {
            // test kimliği: X-User ve isteğe bağlı X-Stamp başlıklarından
            if (context.Request.Headers.TryGetValue("X-User", out var user))
            {
                var claims = new List<Claim> { new("sub", user.ToString()) };
                if (context.Request.Headers.TryGetValue("X-Stamp", out var stamp))
                    claims.Add(new Claim(TokenService.SecurityStampClaimType, stamp.ToString()));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            }

            await next(context);
        });
        app.UseCanTenantResolution();
        app.UseCanSecurityStampValidation();
        app.MapGet("/me", (HttpContext http) => http.User.FindFirst("sub")?.Value ?? "anonim");
        await app.StartAsync();
        return (app, app.GetTestClient(), validator);
    }

    private static HttpRequestMessage Get(string? user, string? stamp)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        if (user is not null)
            request.Headers.Add("X-User", user);
        if (stamp is not null)
            request.Headers.Add("X-Stamp", stamp);
        return request;
    }

    [Fact]
    public async Task Current_stamp_passes_and_old_stamp_ends_the_session()
    {
        (WebApplication app, HttpClient client, FixedValidator validator) = await CreateAsync();
        await using (app)
        {
            Assert.Equal("42", await (await client.SendAsync(Get("42", "YENI"))).Content.ReadAsStringAsync());

            HttpResponseMessage revoked = await client.SendAsync(Get("42", "ESKI"));
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.Equal("session_revoked", (await revoked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            // token cookie'leri silinir (tarayıcı eski token'ı göndermeyi bırakır)
            Assert.Contains(revoked.Headers.GetValues("Set-Cookie"), c => c.StartsWith("can_access_token=;", StringComparison.Ordinal));

            int calls = validator.Calls;
            Assert.Equal("anonim", await (await client.SendAsync(Get(null, null))).Content.ReadAsStringAsync()); // anonim: denetim yok
            Assert.Equal("42", await (await client.SendAsync(Get("42", null))).Content.ReadAsStringAsync()); // damgasız eski token: denetlenmez
            Assert.Equal(calls, validator.Calls);
        }
    }
}

public class AccessTokenRevocationTests
{
    [Fact]
    public async Task Logged_out_token_is_rejected_until_it_would_expire()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanAccessTokenRevocation();

        await using WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.Use(async (context, next) =>
        {
            // test kimliği: X-Jti ve X-Exp (unix saniye) başlıklarından
            if (context.Request.Headers.TryGetValue("X-Jti", out var jti))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("sub", "42"), new Claim("jti", jti.ToString()), new Claim("exp", context.Request.Headers["X-Exp"].ToString())],
                    "Test"));
            }

            await next(context);
        });
        app.UseCanAccessTokenRevocation();
        app.MapGet("/me", () => "ok");
        app.MapPost("/logout", async (HttpContext http) =>
        {
            await http.RevokeCurrentAccessTokenAsync();
            return TypedResults.NoContent();
        });
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        HttpRequestMessage As(HttpMethod method, string path, string jti, DateTimeOffset expires)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Jti", jti);
            request.Headers.Add("X-Exp", expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return request;
        }

        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(15);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Get, "/me", "token-a", expires))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(As(HttpMethod.Post, "/logout", "token-a", expires))).StatusCode);

        HttpResponseMessage reused = await client.SendAsync(As(HttpMethod.Get, "/me", "token-a", expires));
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal("session_revoked", (await reused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // aynı kullanıcının başka oturumu (başka jti) etkilenmez
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Get, "/me", "token-b", expires))).StatusCode);

        // süresi çoktan dolmuş token listeye yazılmaz (liste büyümez)
        IAccessTokenDenylist denylist = app.Services.GetRequiredService<IAccessTokenDenylist>();
        await denylist.RevokeAsync("old", DateTimeOffset.UtcNow.AddHours(-1));
        Assert.False(await denylist.IsRevokedAsync("old"));
    }
}
