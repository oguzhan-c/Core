using Can.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.MultiTenancy;

public sealed class MultiTenancyOptions
{
    /// <summary>
    /// Ortak veritabanı. Tek veritabanlı kurulumda tek bağlantı dizesidir; tenant başına veritabanı kullanırken
    /// kendi veritabanı olmayan tenant'lar ve tenant'sız istekler (ör. host yönetimi) bunu kullanır.
    /// </summary>
    public string? DefaultConnectionString { get; set; }

    /// <summary>
    /// Sabit tenant listesi (ör. appsettings'ten). Doluysa <see cref="InMemoryTenantStore"/> kaydedilir;
    /// tenant'lar veritabanındaysa boş bırak ve kendi <see cref="ITenantStore"/>'unu kaydet.
    /// </summary>
    public List<TenantInfo> Tenants { get; set; } = [];

    /// <summary>
    /// Tenant kimliğini entity'lerdeki <c>TenantId</c> tipine çevirir. Varsayılan: <see cref="Guid"/>.
    /// int için: <c>v =&gt; int.TryParse(v, out int id) ? id : null</c>.
    /// </summary>
    public Func<string, object?> TenantIdParser { get; set; } = value => Guid.TryParse(value, out Guid id) ? id : null;
}

public static class MultiTenancyServiceCollectionExtensions
{
    /// <summary>
    /// Çoklu kiracılık altyapısını kaydeder. Birden fazla çağrılırsa son ayarlar geçerlidir.
    /// </summary>
    /// <example>
    /// Tek veritabanı:
    /// <code>
    /// services.AddCanMultiTenancy(o =&gt; o.DefaultConnectionString = config.GetConnectionString("Default"));
    /// services.AddCanPersistence&lt;AppDbContext&gt;(o =&gt; o.UseNpgsql(config.GetConnectionString("Default")));
    /// </code>
    /// Tenant başına veritabanı (karma da olabilir):
    /// <code>
    /// services.AddCanMultiTenancy(o =&gt;
    /// {
    ///     o.DefaultConnectionString = config.GetConnectionString("Default");
    ///     config.GetSection("Tenants").Bind(o.Tenants);
    /// });
    /// services.AddCanPersistence&lt;AppDbContext&gt;((sp, o) =&gt;
    ///     o.UseNpgsql(sp.GetRequiredService&lt;ITenantConnectionStringResolver&gt;().Resolve()));
    /// </code>
    /// </example>
    public static IServiceCollection AddCanMultiTenancy(this IServiceCollection services, Action<MultiTenancyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MultiTenancyOptions();
        configure?.Invoke(options);

        services.RemoveAll<MultiTenancyOptions>();
        services.AddSingleton(options);

        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantConnectionStringResolver, TenantConnectionStringResolver>();

        // Diğer paketlerin "tenant yok" varsayılanının yerine geçer.
        services.RemoveAll<ICurrentTenant>();
        services.AddScoped<ICurrentTenant, CurrentTenant>();

        if (options.Tenants.Count > 0)
        {
            services.RemoveAll<ITenantStore>();
            services.AddSingleton<ITenantStore>(new InMemoryTenantStore(options.Tenants));
        }

        return services;
    }

    /// <summary>
    /// Belirli bir tenant adına çalışan bir DI scope'u açar (arka plan işleri, migration, seed ...).
    /// Scope içindeki DbContext, repository ve audit o tenant'ı kullanır.
    /// </summary>
    public static AsyncServiceScope CreateTenantScope(this IServiceProvider serviceProvider, TenantInfo tenant)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(tenant);

        AsyncServiceScope scope = serviceProvider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
        return scope;
    }
}
