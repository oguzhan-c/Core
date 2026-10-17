#if (MultiTenancy)
using Can.Core.MultiTenancy;
#endif
#if (AuditTrail)
using Can.Core.Persistence.AuditTrail;
#endif
using Can.Core.Persistence.DependencyInjection;
#if (MultiTenancy)
using Can.Core.Persistence.MultiTenancy;
#endif
using Can.Core.Persistence.RequestLogging;
using Can.Core.Persistence.Retention;
using Can.Core.Persistence.Seeding;
#if (Reporting)
using Can.Core.Reporting;
using Can.Core.Reporting.EntityFrameworkCore;
#endif
using Can.Core.Security.Entities;
using Can.Core.Security.EntityFrameworkCore;
using Can.Core.Security.Events;
#if (Settings)
using Can.Core.Settings;
using Can.Core.Settings.EntityFrameworkCore;
#endif
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
#if (MultiTenancy)
using CanAppTemplate.Application.Services.Repositories;
#endif
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;
using CanAppTemplate.Persistence.Seeding;
using CanAppTemplate.Persistence.Contexts;
#if (MultiTenancy)
using CanAppTemplate.Persistence.Repositories;
#endif

namespace CanAppTemplate.Persistence;

public static class PersistenceServiceRegistration
{
    /// <summary>
    /// EF Core (DbContext, repository'ler, unit of work), güvenlik depoları (oturum, cihaz, güvenlik olayı) ve seed.
    /// </summary>
    /// <remarks>appsettings: <c>ConnectionStrings:CanAppTemplate</c>, <c>Seed</c>, <c>QueryDiagnostics</c>, <c>DataRetention</c>.</remarks>
    public static IServiceCollection AddPersistenceServices(this IServiceCollection services, IConfiguration configuration)
    {
        // TODO(can-gen): Bağlantı dizesini user-secrets'a yaz (appsettings'e şifre koyma):
        //   dotnet user-secrets set "ConnectionStrings:CanAppTemplate" "<bağlantı dizesi>" --project src/CanAppTemplate.WebApi
        string connectionString =
            configuration.GetConnectionString("CanAppTemplate") ?? throw new InvalidOperationException("ConnectionStrings:CanAppTemplate ayarı eksik.");
#if (MultiTenancy)

        // Tenant'lar appsettings'teki "MultiTenancy:Tenants" listesinden okunur. Kendi veritabanı (ConnectionString) olan
        // tenant ona, olmayan ortak veritabanına bağlanır ve verisi TenantId filtresiyle ayrılır.
        // TODO(can-gen): Tenant'lar veritabanında tutulacaksa listeyi boş bırak ve kendi ITenantStore'unu kaydet.
        services.AddCanMultiTenancy(o =>
        {
            o.DefaultConnectionString = connectionString;
            configuration.GetSection("MultiTenancy:Tenants").Bind(o.Tenants);
        });
#endif

        services.AddCanPersistence<BaseDbContext>(
#if (MultiTenancy)
            (serviceProvider, options) =>
            {
                string tenantConnectionString = serviceProvider.GetRequiredService<ITenantConnectionStringResolver>().Resolve();
#else
            (_, options) =>
            {
                string tenantConnectionString = connectionString;
#endif
#if (UsePostgreSql)
                options.UseNpgsql(tenantConnectionString, npgsql => npgsql.EnableRetryOnFailure());
#elif (UseSqlServer)
                options.UseSqlServer(tenantConnectionString, sql => sql.EnableRetryOnFailure());
#else
                options.UseSqlite(tenantConnectionString);
#endif
                // Aggregate'ler arası ilişkiler navigation'sız olabilir; filtreli zorunlu ilişki uyarısı burada geçersiz.
                options.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
            }
        );

#if (AuditTrail)
        services.AddCanAuditTrail();
#endif
#if (EventBus)
        services.AddCanOutbox<BaseDbContext>(o => configuration.GetSection("Outbox").Bind(o));
#endif
        services.AddCanEntityCacheInvalidation();
        services.AddCanQueryDiagnostics(o => configuration.GetSection("QueryDiagnostics").Bind(o));

        // Güvenlik: oturumlar, kayıtlı cihazlar, damga doğrulayıcı ve güvenlik olaylarının veritabanı kaydı.
        services.AddCanUserSessions<BaseDbContext, Guid>();
        services.AddCanTrustedDevices<BaseDbContext, Guid>();
        services.AddCanSecurityStampValidator<BaseDbContext, AppUser, Guid>();
        services.AddCanSecurityEventStore<BaseDbContext>();

        var seed = new SeedOptions();
        configuration.GetSection("Seed").Bind(seed);
        services.AddSingleton(seed);
        services.AddCanDataSeeders(typeof(PersistenceServiceRegistration).Assembly);
#if (Settings)

        // Ayar değerleri veritabanında (tanımlar Application'da: AppSettings).
        services.AddCanSettings(s => s.UseEntityFrameworkStore<BaseDbContext>());
#endif
#if (Reporting)

        // Raporlar: veri kaynakları ve kayıtlı raporlar (uçlar WebApi'de: /api/reporting).
        // TODO(can-gen): Rapor veri kaynaklarını ekle: .AddSource("orders", sp => sp.GetRequiredService<BaseDbContext>().Orders.Select(...), o => o.Field(...))
        services.AddCanReporting().UseEntityFrameworkStore<BaseDbContext>();
#endif
#if (MultiTenancy)

        services.AddScoped<ICrossTenantReader, CrossTenantReader>();
#endif
        // Entity repository'leri (can-gen her entity için bir satır ekler).
        // can-gen:repositories

        AddDataRetention(services, configuration.GetSection("DataRetention"));
        return services;
    }

    /// <summary>İstek aktivite logunu veritabanına yazar (WebApi'de <c>RequestActivityLog:Enabled</c> açıksa çağrılır).</summary>
    public static IServiceCollection AddRequestActivityStore(this IServiceCollection services) =>
        services.AddCanRequestActivityStore<BaseDbContext>();

    /// <summary>
    /// Veri saklama: VARSAYILAN KAPALI (kayıtların tamamı saklanır). <c>DataRetention:Enabled = true</c> ile açılır;
    /// süreler gün olarak ayarlanır, <c>DryRun</c> ile önce yalnızca sayılır.
    /// </summary>
    private static void AddDataRetention(IServiceCollection services, IConfigurationSection section)
    {
        if (!section.GetValue("Enabled", false))
            return;

        int Days(string key, int fallback) => section.GetValue($"{key}Days", fallback);

        services.AddCanDataRetention<BaseDbContext>(o =>
        {
            o.DryRun = section.GetValue("DryRun", true);
#if (AuditTrail)
            o.Keep<AuditLog>(TimeSpan.FromDays(Days("AuditLogs", 365)), a => a.Timestamp);
#endif
            o.Keep<SecurityEventRecord>(TimeSpan.FromDays(Days("SecurityEvents", 180)), e => e.OccurredAt);
            o.Keep<SecurityEventRecord>(TimeSpan.FromDays(Days("SuccessfulLogins", 30)), e => e.OccurredAt, where: e => e.Type == SecurityEventTypes.LoginSucceeded);
            o.Keep<RequestActivityRecord>(TimeSpan.FromDays(Days("RequestActivities", 90)), a => a.Timestamp);
            o.Keep<RefreshToken<Guid>>(TimeSpan.FromDays(Days("ExpiredRefreshTokens", 30)), t => t.ExpiresAt);
            o.Keep<TrustedDevice<Guid>>(TimeSpan.FromDays(Days("RevokedDevices", 90)), d => d.RevokedAt);
            // TODO(can-gen): Zamanla birikip silinebilecek uygulama kayıtlarını (bildirim, log ...) buraya ekle.
        });
    }

    /// <summary>
    /// Veritabanını hazırlar ve başlangıç verisini yükler. Migration varsa uygulanır; yoksa şema doğrudan oluşturulur
    /// (ilk deneme için; kalıcı ortamlarda migration kullan: <c>dotnet ef migrations add Initial</c>).
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
#if (MultiTenancy)
        await services.InitializeTenantDatabasesAsync<BaseDbContext>(PrepareAsync, cancellationToken);
#else
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
            await PrepareAsync(scope.ServiceProvider.GetRequiredService<BaseDbContext>(), cancellationToken);
#endif

        await services.SeedDataAsync(cancellationToken: cancellationToken);

        static async Task PrepareAsync(BaseDbContext db, CancellationToken ct)
        {
            if (db.Database.GetMigrations().Any())
                await db.Database.MigrateAsync(ct);
            else
                await db.Database.EnsureCreatedAsync(ct);
        }
    }
}
