using Can.Core.MultiTenancy;
using Can.Core.Persistence.DependencyInjection;
using Can.Core.Persistence.MultiTenancy;
using Can.Core.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Northwind.Application.Common;
using Northwind.Infrastructure.Identity;
using Northwind.Infrastructure.Persistence;
using Northwind.Infrastructure.Seeding;

namespace Northwind.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// PostgreSQL + EF Core, tek veritabanında çok mağazalı (tenant) yapı, outbox, değişiklik geçmişi ve seed.
    /// </summary>
    /// <remarks>
    /// appsettings: <c>ConnectionStrings:Northwind</c>, <c>Tenants</c> (mağaza listesi), <c>Outbox</c>, <c>Seed</c>.
    /// </remarks>
    public static IServiceCollection AddNorthwindInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString =
            configuration.GetConnectionString("Northwind")
            ?? throw new InvalidOperationException("ConnectionStrings:Northwind ayarı eksik.");

        services.AddCanMultiTenancy(o =>
        {
            o.DefaultConnectionString = connectionString;
            configuration.GetSection("Tenants").Bind(o.Tenants);
        });

        services.AddCanPersistence<NorthwindDbContext>(
            (serviceProvider, options) =>
                options
                    .UseNpgsql(
                        serviceProvider.GetRequiredService<ITenantConnectionStringResolver>().Resolve(),
                        npgsql => npgsql.EnableRetryOnFailure()
                    )
                    // Aggregate'ler arası ilişkiler navigation'sız; filtreli zorunlu ilişki uyarısı burada geçersiz.
                    .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
        );

        services.AddCanAuditTrail();
        services.AddCanOutbox<NorthwindDbContext>(o => configuration.GetSection("Outbox").Bind(o));

        var seed = new SeedOptions();
        configuration.GetSection("Seed").Bind(seed);
        services.AddSingleton(seed);
        services.AddCanDataSeeders(typeof(DependencyInjection).Assembly);

        services.AddScoped<IIdentityStore, IdentityStore>();
        services.AddScoped<IAuditLogReader, AuditLogReader>();
        services.AddScoped<IOutboxMonitor, OutboxMonitor>();

        return services;
    }

    /// <summary>
    /// Veritabanını hazırlar ve başlangıç verisini yükler. Migration varsa uygulanır; henüz migration
    /// oluşturulmadıysa şema doğrudan oluşturulur (yalnızca ilk deneme için; kalıcı ortamlarda migration kullan).
    /// </summary>
    public static async Task InitializeNorthwindDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await services.InitializeTenantDatabasesAsync<NorthwindDbContext>(
            async (db, ct) =>
            {
                if (db.Database.GetMigrations().Any())
                    await db.Database.MigrateAsync(ct);
                else
                    await db.Database.EnsureCreatedAsync(ct);
            },
            cancellationToken
        );

        await services.SeedDataAsync(cancellationToken: cancellationToken);
    }
}
