using Can.Core.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.MultiTenancy;

public static class TenantDatabaseExtensions
{
    /// <summary>
    /// Ortak veritabanını ve kendi veritabanı olan her aktif tenant'ın veritabanını hazırlar.
    /// Varsayılan olarak migration'ları uygular (<c>Database.MigrateAsync</c>).
    /// </summary>
    /// <param name="services">Uygulamanın kök servis sağlayıcısı (ör. <c>app.Services</c>).</param>
    /// <param name="initialize">Her veritabanı için çalışacak işlem; ör. testlerde <c>EnsureCreatedAsync</c>, ya da seed.</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    /// <example>
    /// <code>
    /// var app = builder.Build();
    /// await app.Services.InitializeTenantDatabasesAsync&lt;AppDbContext&gt;();
    /// </code>
    /// </example>
    /// <remarks>
    /// Aynı bağlantı dizesini paylaşan tenant'lar için işlem bir kez çalışır. Her veritabanı kendi DI scope'unda
    /// ve ilgili tenant adına hazırlanır; böylece seed verilerine doğru TenantId yazılır.
    /// </remarks>
    public static async Task InitializeTenantDatabasesAsync<TContext>(
        this IServiceProvider services,
        Func<TContext, CancellationToken, Task>? initialize = null,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        initialize ??= (context, ct) => context.Database.MigrateAsync(ct);

        var initialized = new HashSet<string>(StringComparer.Ordinal);
        MultiTenancyOptions options = services.GetRequiredService<MultiTenancyOptions>();

        // Ortak veritabanı (tenant'sız)
        if (!string.IsNullOrWhiteSpace(options.DefaultConnectionString))
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            await initialize(scope.ServiceProvider.GetRequiredService<TContext>(), cancellationToken).ConfigureAwait(false);
            initialized.Add(options.DefaultConnectionString);
        }

        // Kendi veritabanı olan tenant'lar
        ITenantStore? store = services.GetService<ITenantStore>();
        if (store is null)
            return;

        foreach (TenantInfo tenant in await store.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!tenant.IsActive || !tenant.HasDedicatedDatabase || !initialized.Add(tenant.ConnectionString!))
                continue;

            await using AsyncServiceScope scope = services.CreateTenantScope(tenant);
            await initialize(scope.ServiceProvider.GetRequiredService<TContext>(), cancellationToken).ConfigureAwait(false);
        }
    }
}
