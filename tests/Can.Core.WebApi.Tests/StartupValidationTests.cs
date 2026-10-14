using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.Idempotency;
using Can.Core.WebApi.RateLimiting;
using Can.Core.WebApi.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Tests;

/// <summary>Eksik ya da hatalı yapılandırma ilk istekte değil, açılışta ve anlaşılır bir mesajla ortaya çıkar.</summary>
public class StartupValidationTests
{
    private static WebApplicationBuilder Builder()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        return builder;
    }

    [Fact]
    public async Task Jwt_authentication_without_security_fails_at_startup_listing_the_fix()
    {
        WebApplicationBuilder builder = Builder();
        builder.Services.AddCanJwtAuthentication(); // AddCanSecurity unutuldu

        await using WebApplication app = builder.Build();
        app.MapGet("/", () => "ok");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("AddCanJwtAuthentication", error.Message, StringComparison.Ordinal);
        Assert.Contains("AddCanSecurity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cross_site_cookies_must_be_secure()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Builder().Services.AddCanJwtAuthentication(o => { o.SameSite = SameSiteMode.None; o.Secure = false; }));
        Assert.Contains("SameSite = None", error.Message, StringComparison.Ordinal);

        InvalidOperationException names = Assert.Throws<InvalidOperationException>(() =>
            Builder().Services.AddCanJwtAuthentication(o => { o.RefreshTokenCookieName = o.AccessTokenCookieName; o.RefreshTokenPath = "auth/refresh"; }));
        Assert.Contains("aynı olamaz", names.Message, StringComparison.Ordinal);
        Assert.Contains("RefreshTokenPath", names.Message, StringComparison.Ordinal); // tüm hatalar tek mesajda
    }

    [Fact]
    public void Middleware_without_its_registration_fails_while_building_the_pipeline()
    {
        WebApplication app = Builder().Build();

        Assert.Contains("AddCanSecurityStampValidator", Assert.Throws<InvalidOperationException>(() => app.UseCanSecurityStampValidation()).Message, StringComparison.Ordinal);
        Assert.Contains("AddCanAccessTokenRevocation", Assert.Throws<InvalidOperationException>(() => app.UseCanAccessTokenRevocation()).Message, StringComparison.Ordinal);
        Assert.Contains("AddCanIdempotency", Assert.Throws<InvalidOperationException>(() => app.UseCanIdempotency()).Message, StringComparison.Ordinal);
        Assert.Contains("AddCanRateLimiting", Assert.Throws<InvalidOperationException>(() => app.UseCanRateLimiting()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_limits_are_rejected_with_every_problem_listed()
    {
        InvalidOperationException rateLimit = Assert.Throws<InvalidOperationException>(() => Builder().Services.AddCanRateLimiting(o =>
        {
            o.Auth.PermitLimit = 0;
            o.Sensitive.Window = TimeSpan.Zero;
        }));
        Assert.Contains("Auth.PermitLimit", rateLimit.Message, StringComparison.Ordinal);
        Assert.Contains("Sensitive.Window", rateLimit.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => Builder().Services.AddCanIdempotency(o => o.Expiration = TimeSpan.Zero));

        // kapalı sınırlayıcının kuralları denetlenmez
        Builder().Services.AddCanRateLimiting(o => { o.Enabled = false; o.Auth.PermitLimit = 0; });
    }
}
