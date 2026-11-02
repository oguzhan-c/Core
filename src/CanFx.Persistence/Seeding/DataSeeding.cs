using System.Reflection;
using CanFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CanFx.Persistence.Seeding;

/// <summary>Seed işleminin bağlamı.</summary>
public sealed class DataSeedContext
{
    public DataSeedContext(TenantInfo? tenant) => Tenant = tenant;

    /// <summary>Seed edilen tenant; <see langword="null"/> ise host (tenant'sız veriler: roller, tenant listesi ...).</summary>
    public TenantInfo? Tenant { get; }

    public bool IsHost => Tenant is null;
}

/// <summary>
/// Başlangıç verisi ekleyen sınıf. Her açılışta çalışabileceği için idempotent olmalı (önce var mı diye bak).
/// </summary>
/// <example>
/// <code>
/// public sealed class RoleSeeder(AppDbContext db) : IDataSeeder
/// {
///     public async Task SeedAsync(DataSeedContext context, CancellationToken ct)
///     {
///         if (!context.IsHost || await db.Roles.AnyAsync(ct)) return;
///         db.Roles.Add(new Role&lt;Guid&gt;(Guid.CreateVersion7(), "Admin"));
///         await db.SaveChangesAsync(ct);
///     }
/// }
/// </code>
/// </example>
public interface IDataSeeder
{
    /// <summary>Küçük değer önce çalışır (ör. önce kategoriler, sonra ürünler).</summary>
    int Order => 0;

    /// <summary>
    /// <see langword="true"/>: örnek (sahte) veri; yalnızca geliştirmede eklenir (<c>SeedDataAsync(includeDevelopmentData: true)</c>).
    /// <see langword="false"/> (varsayılan): uygulamanın çalışması için gereken veri (roller, para birimleri ...); her ortamda.
    /// </summary>
    bool DevelopmentOnly => false;

    /// <summary>Scope ilgili tenant adına açılır; eklenen kayıtlara <c>TenantId</c> otomatik yazılır.</summary>
    Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken);
}

public static class DataSeedingExtensions
{
    /// <summary>Verilen assembly'lerdeki <see cref="IDataSeeder"/> sınıflarını scoped olarak kaydeder.</summary>
    public static IServiceCollection AddCanDataSeeders(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        IEnumerable<Type> seeders = assemblies
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(IDataSeeder).IsAssignableFrom(t));

        foreach (Type seeder in seeders)
            services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IDataSeeder), seeder));

        return services;
    }

    /// <summary>
    /// Seeder'ları önce host için, sonra (<paramref name="includeTenants"/> ise ve <see cref="ITenantStore"/> kayıtlıysa)
    /// her aktif tenant için kendi scope'unda çalıştırır. Migration'lardan SONRA çağır.
    /// </summary>
    /// <example>
    /// <code>
    /// await app.Services.InitializeTenantDatabasesAsync&lt;AppDbContext&gt;();
    /// await app.Services.SeedDataAsync();
    /// </code>
    /// </example>
    /// <param name="services">Uygulamanın servisleri.</param>
    /// <param name="includeTenants">Her aktif tenant için de çalıştır.</param>
    /// <param name="includeDevelopmentData">Örnek veri seeder'larını da çalıştır (<see cref="IDataSeeder.DevelopmentOnly"/>); geliştirmede true.</param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task SeedDataAsync(
        this IServiceProvider services,
        bool includeTenants = true,
        bool includeDevelopmentData = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            await RunAsync(scope.ServiceProvider, new DataSeedContext(null), includeDevelopmentData, cancellationToken).ConfigureAwait(false);
        }

        if (!includeTenants || services.GetService<ITenantStore>() is not { } store)
            return;

        foreach (TenantInfo tenant in await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!tenant.IsActive)
                continue;

            await using AsyncServiceScope scope = services.CreateTenantScope(tenant);
            await RunAsync(scope.ServiceProvider, new DataSeedContext(tenant), includeDevelopmentData, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunAsync(IServiceProvider services, DataSeedContext context, bool includeDevelopmentData, CancellationToken cancellationToken)
    {
        IEnumerable<IDataSeeder> seeders = services
            .GetServices<IDataSeeder>()
            .Where(s => includeDevelopmentData || !s.DevelopmentOnly)
            .OrderBy(s => s.Order)
            .ThenBy(s => s.GetType().FullName, StringComparer.Ordinal);

        foreach (IDataSeeder seeder in seeders)
            await seeder.SeedAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
