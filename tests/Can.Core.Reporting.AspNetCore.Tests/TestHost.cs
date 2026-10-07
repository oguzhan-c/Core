using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Can.Core.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Can.Core.Reporting.AspNetCore.Tests;

public sealed record SaleRow(string Category, string Country, decimal Amount, DateTime OrderDate);

/// <summary>Testte değiştirilebilen kullanıcı ve tenant (istekler arasında rol değiştirmek için).</summary>
public sealed class TestIdentity : ICurrentUser, ICurrentTenant
{
    public string? Id { get; set; } = "u1";

    public string? UserName { get; set; } = "ayse";

    public string? Email => null;

    public IReadOnlyCollection<string> Roles { get; set; } = [];

    public IReadOnlyCollection<string> Permissions { get; set; } = ["reports.sales"];

    public string? Tenant { get; set; } = "t1";

    object? ICurrentTenant.Id => Tenant;

    public void As(string id, string? tenant = "t1", string[]? permissions = null, string[]? roles = null)
    {
        Id = id;
        UserName = id;
        Tenant = tenant;
        Permissions = permissions ?? ["reports.sales"];
        Roles = roles ?? [];
    }
}

internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TestIdentity identity)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (identity.Id is null)
            return Task.FromResult(AuthenticateResult.NoResult());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, identity.Id)], "Test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
    }
}

internal sealed class ReportingHost : IAsyncDisposable
{
    public static readonly SaleRow[] Sales =
    [
        new("İçecek", "TR", 50m, new DateTime(2024, 1, 15)),
        new("İçecek", "DE", 36m, new DateTime(2024, 2, 10)),
        new("Şekerleme", "TR", 60m, new DateTime(2024, 7, 20)),
        new("Çeşni", "DE", 40m, new DateTime(2025, 5, 30)),
    ];

    private ReportingHost(WebApplication app, TestIdentity identity)
    {
        App = app;
        Identity = identity;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public TestIdentity Identity { get; }

    public HttpClient Client { get; }

    public static async Task<ReportingHost> StartAsync(Action<CanReportingEndpointOptions>? configure = null)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var identity = new TestIdentity();
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<ICurrentUser>(identity);
        builder.Services.AddSingleton<ICurrentTenant>(identity);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization();

        builder.Services.AddCanReporting(o => o.Culture = CultureInfo.GetCultureInfo("tr-TR"))
            .AddSource("sales", _ => Sales.AsQueryable(), o =>
            {
                o.Caption = "Satışlar";
                o.Permission = "reports.sales";
                o.DatabaseMode = false;
                o.Field("amount", "Tutar", "N2");
            })
            .AddSource("payroll", _ => Sales.AsQueryable(), o =>
            {
                o.Caption = "Bordro";
                o.Permission = "reports.payroll";
                o.DatabaseMode = false;
            });

        WebApplication app = builder.Build();
        app.UseRequestLocalization("tr-TR");
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCanReporting("/api/reporting", configure);
        await app.StartAsync();
        return new ReportingHost(app, identity);
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
