using System.Text.Json.Serialization;
using Can.Core.Application.Features;
#if (Hangfire)
using Can.Core.BackgroundJobs.Hangfire;
#endif
#if (Redis)
using Can.Core.Caching.Redis;
#endif
#if (EventBus)
using Can.Core.EventBus;
#endif
using Can.Core.Localization;
using Can.Core.Logging.Serilog;
#if (OpenTelemetry)
using Can.Core.Observability.OpenTelemetry;
#endif
using Can.Core.Persistence.HealthChecks;
#if (Realtime)
using Can.Core.Realtime.SignalR;
#endif
#if (Search)
using Can.Core.Search;
#endif
using Can.Core.Security.DependencyInjection;
using Can.Core.Security.Events;
using Can.Core.Security.Passkeys;
#if (Reporting)
using Can.Core.Reporting.AspNetCore;
#endif
#if (Settings)
using Can.Core.Settings;
using Can.Core.Settings.AspNetCore;
#endif
using Can.Core.WebApi.Caching;
using Can.Core.WebApi.Features;
using Can.Core.WebApi.DependencyInjection;
#if (ExternalLogin)
using Can.Core.WebApi.ExternalLogin;
#endif
using Can.Core.WebApi.HealthChecks;
using Can.Core.WebApi.Idempotency;
using Can.Core.WebApi.OpenApi;
using Can.Core.WebApi.RateLimiting;
using Can.Core.WebApi.RequestLogging;
using Can.Core.WebApi.Security;
using Can.Core.WebApi.Versioning;
using Microsoft.AspNetCore.HttpOverrides;
using CanAppTemplate.Application;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Persistence;
using CanAppTemplate.Persistence.Contexts;
using CanAppTemplate.Infrastructure;
using CanAppTemplate.WebAPI.Security;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
IConfiguration config = builder.Configuration;

// ================================================================ servisler

builder.AddCanSerilog(o =>
{
    o.ApplicationName = "CanAppTemplate";
    o.FilePath = config["Logs:FilePath"];
});
#if (OpenTelemetry)

// İz ve metrikler (OTLP): docker compose --profile observability up -d → http://localhost:18888
if (config.GetValue("OpenTelemetry:Enabled", false))
{
    builder.Services.AddCanOpenTelemetry(o =>
    {
        o.ServiceName = "canapptemplate";
        o.ServiceVersion = typeof(Program).Assembly.GetName().Version?.ToString();
        if (config["OpenTelemetry:Endpoint"] is { Length: > 0 } endpoint)
            o.OtlpEndpoint = new Uri(endpoint);
#if (UsePostgreSql)
        o.AdditionalSources.Add("Npgsql");
        o.AdditionalMeters.Add("Npgsql");
#endif
    });
}
#endif

// TODO(can-gen): Desteklenen dilleri ve varsayılan dili ayarla (çeviriler: Localization/*.json).
builder.Services.AddCanLocalization(o =>
{
    o.DefaultCulture = "tr";
    o.SupportedCultures = ["tr", "en"];
});

// Katmanlar
builder.Services.AddApplicationServices();
builder.Services.AddPersistenceServices(config);

// Kimlik: JWT (HttpOnly cookie'de; header'dan okunmaz), e-posta kodları, kurtarma kodları, cihaz bağlama, passkey.
// Anahtarlar user-secrets'ta:
//   dotnet user-secrets set "Security:Jwt:SigningKey" "<en az 32 karakter>"
//   dotnet user-secrets set "Security:VerificationCodeKey" "<en az 32 karakter, JWT anahtarından farklı>"
builder.Services.AddCanSecurity(o =>
{
    config.GetSection("Security:Jwt").Bind(o.Jwt);
    o.VerificationCodeKey = config["Security:VerificationCodeKey"];

    // Passkey (WebAuthn): bölüm yoksa passkey uçları hiç açılmaz.
    if (config.GetSection("Security:Passkey").Exists())
    {
        o.Passkey = new PasskeyOptions();
        config.GetSection("Security:Passkey").Bind(o.Passkey);
    }
});
builder.Services.AddCanSecretProtection();          // TOTP anahtarları veritabanında şifreli
builder.Services.AddCanSecurityEvents();            // log + metrik (veritabanı kaydı Persistence'ta)
builder.Services.AddCanAccessTokenRevocation();     // çıkış / oturum kapatma: access token hemen 401
builder.Services.AddCanWebApi();
builder.Services.AddControllers();
builder.Services.AddCanJwtAuthentication(o => config.GetSection("Security:Cookies").Bind(o));
builder.Services.AddAuthorization();
#if (ExternalLogin)

// Google / Microsoft / GitHub ile giriş. Anahtarı boş olan sağlayıcı açılmaz; anahtarlar user-secrets'ta.
builder.Services.AddCanExternalLogin(o => o
    .AddGoogle(config["Security:External:Google:ClientId"] ?? "", config["Security:External:Google:ClientSecret"] ?? "")
    .AddMicrosoft(config["Security:External:Microsoft:ClientId"] ?? "", config["Security:External:Microsoft:ClientSecret"] ?? "")
    .AddGitHub(config["Security:External:GitHub:ClientId"] ?? "", config["Security:External:GitHub:ClientSecret"] ?? ""));
#endif

builder.Services.AddDataProtection();
builder.Services.AddDistributedMemoryCache();       // Redis varsa AddCanRedisCache bunun yerine geçer
builder.Services.AddSingleton<TwoFactorCookie>();
builder.Services.AddSingleton<PasskeyCeremonyStore>();
builder.Services.AddSingleton<ClientInfoFactory>();
builder.Services.AddHttpContextAccessor();
#if (Settings)

// Ayarlar: gizli değerler (IsSecret) veritabanında Data Protection ile şifreli.
builder.Services.AddCanSettings(s => s.UseDataProtection());
#endif

// Dış servisler (Infrastructure): ayarı boş olan yerel karşılığıyla çalışır.
InfrastructureStatus infrastructure = builder.Services.AddInfrastructureServices(config);
// can-gen:webapi-services

// HTTP: hız sınırı, idempotency, çıktı önbelleği + ETag + sıkıştırma, API sürümleri, OpenAPI.
builder.Services.AddCanRateLimiting(o => config.GetSection("RateLimiting").Bind(o));
builder.Services.AddCanIdempotency();
builder.Services.AddCanHttpCaching();
builder.Services.AddCanApiVersioning(o =>
{
    o.Add(new ApiVersionInfo(1));
    o.DefaultVersion = 1;
    // Sürümsüz uçlar (kimlik ve altyapı). Uygulama uçları /api/v1/... altındadır.
    foreach (string path in new[] { "/api/auth", "/api/identity", "/api/sessions", "/api/features", "/api/settings", "/api/reporting" })
        o.UnversionedPaths.Add(path);
});
builder.Services.AddCanOpenApi(o =>
{
    o.Title = "CanAppTemplate API";
    // TODO(can-gen): API açıklamasını yaz.
    o.Description = "CanAppTemplate API (Can.Core).";
});
builder.Services.AddCanFeatureFlags(config.GetSection("Features"));

// İsteğe bağlı (varsayılan KAPALI): istek aktivite logu, güvenlik başlıkları, CORS, CSRF.
bool activityLog = config.GetValue("RequestActivityLog:Enabled", false);
if (activityLog)
{
    builder.Services.AddCanRequestActivityLog(o => config.GetSection("RequestActivityLog").Bind(o));
    builder.Services.AddRequestActivityStore();
}

bool securityHeaders = config.GetValue("BrowserSecurity:SecurityHeaders", false);
if (securityHeaders)
    builder.Services.AddCanSecurityHeaders();

string[] corsOrigins = config.GetSection("BrowserSecurity:CorsOrigins").Get<string[]>() ?? [];
if (corsOrigins.Length > 0)
{
    builder.Services.AddCanCors(o =>
    {
        foreach (string origin in corsOrigins)
            o.AllowedOrigins.Add(origin);
    });
}

bool csrf = config.GetValue("BrowserSecurity:Csrf", false);
if (csrf)
    builder.Services.AddCanCsrfProtection();

// Reverse proxy (nginx, ön yüz sunucusu) arkasında gerçek IP ve şema. Varsayılan olarak yalnızca aynı makinedeki
// (loopback) proxy'ye güvenilir; başka makinedeki proxy için KnownProxies'e adresini ekle.
builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

// Sağlık: /health/live ve /health/ready.
IHealthChecksBuilder health = builder.Services.AddHealthChecks().AddCanDbContextCheck<BaseDbContext>();
#if (EventBus)
health.AddCanOutboxCheck<BaseDbContext>();
if (infrastructure.BrokerEnabled)
    health.AddCanEventBusCheck();
#endif
#if (Redis)
if (infrastructure.Redis is not null)
    health.AddCanRedisCheck();
#endif
#if (Hangfire)
if (infrastructure.HangfireEnabled)
    health.AddCanHangfireCheck();
#endif

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ================================================================ uygulama

WebApplication app = builder.Build();
#if (Search)

// Arama dizinleri veriden ÖNCE eşlemesiyle kurulur; arama motoruna ulaşılamazsa uygulama yine açılır.
try
{
    await app.Services.EnsureCanSearchIndexesAsync();
}
catch (SearchException ex)
{
    app.Logger.LogWarning(ex, "Arama dizini kurulamadı; arama çalışmayabilir.");
}
#endif

if (config.GetValue("Database:InitializeOnStartup", true))
    await app.Services.InitializeDatabaseAsync();

app.UseForwardedHeaders();
app.UseCanRequestLogging();             // her isteğe tek özet satırı: en dışta (gerçek durum kodu)
if (securityHeaders)
    app.UseCanSecurityHeaders();
app.UseCanRequestLocalization();        // ?culture=en → cookie → Accept-Language
app.UseCanExceptionHandler();           // hatalar → ProblemDetails

if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseCanApiVersioning();              // /api/... → /api/v1/... (UseRouting)
if (corsOrigins.Length > 0)
    app.UseCanCors();
if (csrf)
    app.UseCanCsrfProtection();

app.UseAuthentication();
app.UseCanAccessTokenRevocation();      // çıkış yapılmış / kapatılmış oturumun token'ı
#if (MultiTenancy)
app.UseCanTenantResolution();           // token'daki (cookie) tenant_id → aktif tenant
#endif
app.UseCanSecurityStampValidation();    // şifre/2FA değişti: eski token'lar 401
app.UseCanPermissionResolution();       // roller / yetkiler token'dan değil veritabanından (önbellekli): değişiklik hemen geçerli
app.UseCanLogEnrichment();              // loglara UserId / TenantId
app.UseCanRateLimiting();
app.UseAuthorization();
if (activityLog)
    app.UseCanRequestActivityLog();
app.UseCanHttpCaching();
app.UseCanIdempotency();

app.MapCanOpenApi();                    // /openapi/v1.json + /docs (yalnızca Development)
app.MapCanHealthChecks();               // /health/live, /health/ready
app.MapControllers();                   // Controllers: /api/auth, /api/auth/account, /api/v1/{çoğul}
#if (ExternalLogin)
app.MapExternalLoginEndpoints();        // /api/auth/external/...
#endif
app.MapCanSessions("/api/sessions").RequireAuthorization(); // aktif oturumlar, "diğer cihazlardan çıkış"
app.MapCanFeatures("/api/features");
#if (Settings)
app.MapCanSettings("/api/settings", o => o.AdminRole = Roles.Admin);
#endif
#if (Reporting)
app.MapCanReporting("/api/reporting", o => o.AdminRole = Roles.Admin); // kaynaklar, çalıştır, dışa aktar, kayıtlı raporlar
#endif
app.MapFallback("/api/{**path}", () => TypedResults.NotFound()).ExcludeFromDescription(); // bilinmeyen API adresleri 404
#if (Realtime)
app.MapCanRealtimeHub("/hubs/notifications");
#endif
#if (Hangfire)

if (infrastructure.HangfireEnabled)
    app.MapCanHangfireDashboard("/hangfire", Roles.Admin);
#endif

await app.RunAsync();

/// <summary>Entegrasyon testleri için (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
