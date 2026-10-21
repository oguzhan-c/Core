using System.Security.Claims;
using System.Text.Encodings.Web;
using CanFx.WebApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CanFx.Testing.AspNetCore;

/// <summary>
/// Test kimlik doğrulaması: kullanıcı istekteki <c>X-Can-Test-*</c> başlıklarından kurulur (<see cref="TestHttpClientExtensions.AsUser"/>).
/// Claim tipleri <see cref="CanWebApiOptions"/>'tan alınır; böylece <c>ICurrentUser</c>, rol/yetki denetimi ve tenant
/// çözümü üretimdekiyle aynı çalışır. Production ortamında açılmaz.
/// </summary>
public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CanWebApiOptions webApi,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CanTest";
    public const string UserHeader = "X-Can-Test-User";
    public const string RolesHeader = "X-Can-Test-Roles";
    public const string PermissionsHeader = "X-Can-Test-Permissions";
    public const string TenantHeader = "X-Can-Test-Tenant";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (environment.IsProduction())
            throw new InvalidOperationException("Test kimlik doğrulaması Production ortamında kullanılamaz.");

        if (!Request.Headers.TryGetValue(UserHeader, out var user) || string.IsNullOrWhiteSpace(user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(webApi.UserIdClaimTypes[0], user.ToString()), new(webApi.UserNameClaimTypes[0], user.ToString()) };
        claims.AddRange(Split(RolesHeader).Select(r => new Claim(webApi.RoleClaimTypes[0], r)));
        claims.AddRange(Split(PermissionsHeader).Select(p => new Claim(webApi.PermissionClaimTypes[0], p)));
        claims.AddRange(Split(TenantHeader).Select(t => new Claim(webApi.TenantClaimType, t)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, webApi.UserNameClaimTypes[0], webApi.RoleClaimTypes[0]));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    private IEnumerable<string> Split(string header) =>
        Request.Headers[header].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class TestAuthenticationExtensions
{
    /// <summary>
    /// Test kimlik doğrulamasını varsayılan şema yapar (JWT'nin yerine). <c>AddCanWebApi</c>'den sonra çağır.
    /// </summary>
    public static IServiceCollection AddCanTestAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthentication(o =>
        {
            o.DefaultScheme = TestAuthenticationHandler.SchemeName;
            o.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
            o.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
            o.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
        return services;
    }
}

public static class TestHttpClientExtensions
{
    /// <summary>Bu istemcinin istekleri verilen kullanıcı adına gider (aynı istemciyi değiştirir).</summary>
    /// <example><code>client.AsUser("42", roles: ["Admin"], tenant: "acme");</code></example>
    public static HttpClient AsUser(this HttpClient client, string userId, IEnumerable<string>? roles = null, IEnumerable<string>? permissions = null, string? tenant = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        client.AsAnonymous();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserHeader, userId);
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RolesHeader, string.Join(',', roles));
        if (permissions is not null)
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.PermissionsHeader, string.Join(',', permissions));
        if (tenant is not null)
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.TenantHeader, tenant);
        return client;
    }

    /// <summary>Kullanıcı başlıklarını kaldırır (anonim istek).</summary>
    public static HttpClient AsAnonymous(this HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        foreach (string header in new[] { TestAuthenticationHandler.UserHeader, TestAuthenticationHandler.RolesHeader, TestAuthenticationHandler.PermissionsHeader, TestAuthenticationHandler.TenantHeader })
            client.DefaultRequestHeaders.Remove(header);
        return client;
    }
}

/// <summary>
/// TestServer üzerinde uygulama: ortam <c>Testing</c>, test kimlik doğrulaması kayıtlı. Gerçek ağ portu açılmaz.
/// </summary>
/// <example>
/// <code>
/// await using CanTestApp app = await CanTestApp.StartAsync(
///     services =&gt; { services.AddCanWebApi(); services.AddCanTestAuthentication(); ... },
///     web =&gt; { web.UseCanExceptionHandler(); web.UseAuthentication(); web.UseCanTenantResolution(); web.UseAuthorization(); web.MapPost(...); });
/// HttpClient client = app.CreateClient().AsUser("42", roles: ["Admin"]);
/// </code>
/// </example>
public sealed class CanTestApp : IAsyncDisposable
{
    private CanTestApp(WebApplication app) => App = app;

    public WebApplication App { get; }

    public IServiceProvider Services => App.Services;

    public static async Task<CanTestApp> StartAsync(Action<IServiceCollection> configureServices, Action<WebApplication> configureApp, string environment = "Testing")
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        ArgumentNullException.ThrowIfNull(configureApp);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        configureServices(builder.Services);
        WebApplication app = builder.Build();
        configureApp(app);
        await app.StartAsync().ConfigureAwait(false);
        return new CanTestApp(app);
    }

    public HttpClient CreateClient() => App.GetTestClient();

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
