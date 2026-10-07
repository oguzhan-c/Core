using System.Text.Json.Serialization;
using Can.Core.BackgroundJobs;
using Can.Core.BackgroundJobs.Hangfire;
using Can.Core.Caching.Redis;
using Can.Core.Logging.Serilog;
using Can.Core.Mailing.MailKit;
using Can.Core.Mailing.SendGrid;
using Can.Core.Observability.OpenTelemetry;
using Can.Core.Security.DependencyInjection;
using Can.Core.Security.Passkeys;
using Can.Core.WebApi.DependencyInjection;
using Hangfire;
using Hangfire.PostgreSql;
using Northwind.Application;
using Northwind.Application.Features.Products;
using Northwind.Infrastructure;
using Northwind.WebApi.Endpoints;
using Northwind.WebApi.Security;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
IConfiguration config = builder.Configuration;

// ---------------------------------------------------------------- servisler

builder.AddCanSerilog(o =>
{
    o.ApplicationName = "Northwind";
    o.FilePath = builder.Configuration["Logs:FilePath"];
});

// İz ve metrikler (OTLP): docker compose up -d dashboard → http://localhost:18888
if (config.GetValue("OpenTelemetry:Enabled", false))
{
    builder.Services.AddCanOpenTelemetry(o =>
    {
        o.ServiceName = "northwind";
        o.ServiceVersion = typeof(Program).Assembly.GetName().Version?.ToString();
        if (config["OpenTelemetry:Endpoint"] is { Length: > 0 } endpoint)
            o.OtlpEndpoint = new Uri(endpoint);
        o.AdditionalSources.Add("Npgsql"); // SQL komutları span olarak
        o.AdditionalMeters.Add("Npgsql");
    });
}

builder.Services.AddNorthwindInfrastructure(config);
builder.Services.AddNorthwindApplication(o => config.GetSection("Notifications").Bind(o));

builder.Services.AddCanSecurity(o =>
{
    config.GetSection("Security:Jwt").Bind(o.Jwt);
    o.VerificationCodeKey = config["Security:VerificationCodeKey"]; // e-posta doğrulama kodları

    // Passkey (WebAuthn): bölüm yoksa passkey endpoint'leri hiç açılmaz.
    if (config.GetSection("Security:Passkey").Exists())
    {
        o.Passkey = new PasskeyOptions();
        config.GetSection("Security:Passkey").Bind(o.Passkey);
    }
});
builder.Services.AddCanWebApi();
builder.Services.AddCanJwtAuthentication(o => config.GetSection("Security:Cookies").Bind(o));
builder.Services.AddAuthorization();

// İki adımlı girişte bekleyen giriş şifreli cookie'de; passkey challenge'ları sunucu önbelleğinde.
builder.Services.AddDataProtection();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<TwoFactorCookie>();
builder.Services.AddSingleton<PasskeyCeremonyStore>();

// E-posta: Mail:Provider = Pickup | Smtp | SendGrid. Boşsa PickupDirectory doluysa klasör, değilse SMTP.
string mailProvider = config["Mail:Provider"] is { Length: > 0 } provider
    ? provider
    : config["Mail:PickupDirectory"] is { Length: > 0 } ? "Pickup" : "Smtp";

switch (mailProvider.ToUpperInvariant())
{
    case "PICKUP":
        builder.Services.AddCanEmailPickupDirectory(
            config["Mail:PickupDirectory"] is { Length: > 0 } dir ? dir : "mails",
            config["Mail:From"] ?? "no-reply@northwind.local",
            "Northwind"
        );
        break;
    case "SENDGRID":
        // dotnet user-secrets set "Mail:SendGrid:ApiKey" "SG.xxxx"
        builder.Services.AddCanSendGrid(o => config.GetSection("Mail:SendGrid").Bind(o));
        break;
    default:
        builder.Services.AddCanMailKit(o => config.GetSection("Mail:Smtp").Bind(o));
        break;
}

// Redis: bağlantı varsa HybridCache'in ikinci katmanı olur (birden fazla sunucu aynı önbelleği paylaşır).
if (config["Redis:ConnectionString"] is { Length: > 0 } redis)
{
    builder.Services.AddCanRedisCache(o =>
    {
        o.ConnectionString = redis;
        o.InstanceName = "northwind:";
    });
}

// Arka plan işleri: Hangfire açıksa kalıcı kuyruk + cron + /hangfire dashboard; değilse bellek içi kuyruk.
bool hangfireEnabled = config.GetValue("Hangfire:Enabled", false);
if (hangfireEnabled)
{
    string hangfireDb = config.GetConnectionString("Northwind") ?? "";
    builder.Services.AddCanHangfire(
        c => c.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(hangfireDb)),
        o => o.WorkerCount = config.GetValue<int?>("Hangfire:WorkerCount")
    );

    // Her mağaza için günlük "yeniden sipariş" raporu (cron, Hangfire:ReorderReportCron).
    builder.Services.AddCanHangfireRecurringJob<ReorderReportJob>(
        config["Hangfire:ReorderReportCron"] ?? Cron.Daily(7),
        o =>
        {
            o.JobId = "reorder-report";
            o.PerTenant = true;
        }
    );
}
else
{
    builder.Services.AddCanRecurringJob<ReorderReportJob>(o =>
    {
        o.Interval = TimeSpan.FromHours(24);
        o.RunOnStartup = false;
        o.PerTenant = true;
    });
}

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---------------------------------------------------------------- uygulama

WebApplication app = builder.Build();

if (config.GetValue("Database:InitializeOnStartup", true))
    await app.Services.InitializeNorthwindDatabaseAsync();

// İstek özeti en dışta: hata işleyicinin verdiği gerçek durum kodunu (401, 404 ...) loglar.
app.UseCanRequestLogging();
app.UseCanExceptionHandler();

// React uygulaması (ClientApp → npm run build → wwwroot).
app.UseDefaultFiles();
app.UseStaticFiles();

// Geliştirmede http ile çalışılabilsin; diğer ortamlarda her istek https'e yönlendirilir.
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseAuthentication();
app.UseCanTenantResolution();
app.UseCanLogEnrichment(); // loglara UserId / TenantId
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Northwind API")); // /swagger
    app.MapScalarApiReference(); // /scalar

    if (config["Mail:PickupDirectory"] is { Length: > 0 } mailbox)
        app.MapDevMailbox(mailbox); // /api/dev/mailbox
}

app.MapNorthwindEndpoints(); // /api/...

if (hangfireEnabled)
    app.MapCanHangfireDashboard("/hangfire", Northwind.Domain.Identity.Roles.Admin); // yalnızca yöneticiler; tüm tenant'ların işlerini gösterir

// API dışındaki adresler (/, /products, /admin ...) React uygulamasının yönlendiricisine bırakılır.
app.MapFallbackToFile("index.html");

await app.RunAsync();

/// <summary>Entegrasyon testleri için (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
