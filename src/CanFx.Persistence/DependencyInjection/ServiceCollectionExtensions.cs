using CanFx.Application;
using CanFx.BackgroundJobs;
using CanFx.Persistence.Caching;
using CanFx.Persistence.Diagnostics;
using CanFx.Persistence.AuditTrail;
using CanFx.EventBus;
using CanFx.Persistence.Inbox;
using CanFx.Persistence.Interceptors;
using CanFx.Persistence.Outbox;
using CanFx.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CanFx.Persistence.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// DbContext'i interceptor'larla birlikte kaydeder; <see cref="IUnitOfWork"/> ve generic
    /// <see cref="IRepository{TEntity, TId}"/>'yi ekler.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddCanMediator(cfg =&gt; cfg.RegisterServicesFromAssemblyContaining&lt;Program&gt;());
    /// services.AddCanPersistence&lt;AppDbContext&gt;(o =&gt; o.UseNpgsql(connectionString));
    /// </code>
    /// <para>
    /// <see cref="ICurrentUser"/> ve <see cref="ICurrentTenant"/> kayıtlı değilse "boş" implementasyonlar
    /// eklenir; kendi implementasyonlarını bu çağrıdan önce ya da sonra kaydedebilirsin.
    /// </para>
    /// </example>
    public static IServiceCollection AddCanPersistence<TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddCanPersistence<TContext>((_, options) => configure(options));
    }

    /// <summary>Bağlantı ayarları için DI servislerine (ör. IConfiguration) ihtiyaç duyulduğunda.</summary>
    public static IServiceCollection AddCanPersistence<TContext>(
        this IServiceCollection services,
        Action<IServiceProvider, DbContextOptionsBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentUser>(NullCurrentUser.Instance);
        services.TryAddSingleton<ICurrentTenant>(NullCurrentTenant.Instance);
        services.TryAddSingleton(new AuditTrailOptions());

        services.TryAddScoped<SaveChangesState>();
        services.TryAddScoped<AuditingInterceptor>();
        services.TryAddScoped<AuditTrailInterceptor>();
        services.TryAddScoped<DomainEventInterceptor>();

        services.AddDbContext<TContext>(
            (serviceProvider, options) =>
            {
                configure(serviceProvider, options);

                // Sıra önemli: önce audit alanları ve soft delete, sonra geçmiş kaydı, en son event'ler.
                options.AddInterceptors(
                    serviceProvider.GetRequiredService<AuditingInterceptor>(),
                    serviceProvider.GetRequiredService<AuditTrailInterceptor>(),
                    serviceProvider.GetRequiredService<DomainEventInterceptor>()
                );

                // AddCanEntityCacheInvalidation() çağrıldıysa: kaydedilen entity'lerin önbellek etiketleri silinir.
                if (serviceProvider.GetService<EntityCacheInvalidationInterceptor>() is { } cacheInvalidation)
                    options.AddInterceptors(cacheInvalidation, serviceProvider.GetRequiredService<EntityCacheInvalidationTransactionInterceptor>());

                // AddCanQueryDiagnostics() çağrıldıysa: yavaş sorgu, sorgu etiketi, N+1
                if (serviceProvider.GetService<QueryDiagnosticsInterceptor>() is { } diagnostics)
                    options.AddInterceptors(diagnostics);
            }
        );

        services.TryAddScoped<DbContext>(serviceProvider => serviceProvider.GetRequiredService<TContext>());
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));

        return services;
    }

    /// <summary>
    /// Veritabanı tanılaması:
    /// <list type="bullet">
    /// <item>Eşiği (varsayılan 500 ms) aşan komutlar SQL'i ve kaynağıyla uyarı olarak loglanır, sayılır.</item>
    /// <item>Her SQL'in başına kaynağı yazılır (<c>-- can: GetProductsQuery</c>): veritabanının yavaş sorgu raporunda
    /// sorgunun hangi koddan geldiği görünür.</item>
    /// <item>Komut süreleri metrik olarak yayınlanır (<c>can.db.command.duration</c>).</item>
    /// <item>İsteğe bağlı N+1 dedektörü ve istek başına komut sınırı (geliştirmede).</item>
    /// </list>
    /// Parametre değerleri hiçbir zaman loglanmaz (kişisel veri).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanQueryDiagnostics(o =&gt;
    /// {
    ///     o.SlowQueryThreshold = TimeSpan.FromMilliseconds(300);
    ///     o.RepeatedQueryThreshold = builder.Environment.IsDevelopment() ? 10 : 0; // N+1
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanQueryDiagnostics(this IServiceCollection services, Action<QueryDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new QueryDiagnosticsOptions();
        configure?.Invoke(options);
        if (options.SlowQueryThreshold < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configure), "QueryDiagnosticsOptions.SlowQueryThreshold negatif olamaz.");
        ArgumentOutOfRangeException.ThrowIfNegative(options.RepeatedQueryThreshold, nameof(QueryDiagnosticsOptions.RepeatedQueryThreshold));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxCommandsPerScope, nameof(QueryDiagnosticsOptions.MaxCommandsPerScope));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLoggedSqlLength, 100, nameof(QueryDiagnosticsOptions.MaxLoggedSqlLength));

        services.RemoveAll<QueryDiagnosticsOptions>();
        services.AddSingleton(options);
        services.TryAddScoped<QueryDiagnosticsInterceptor>(); // scope başına sayaç (N+1)
        return services;
    }

    /// <summary>
    /// Kaydedilen entity'lerin önbellek etiketlerini (<see cref="CacheTags.Entity{TEntity}"/>, ör. <c>entity:Product</c>)
    /// kendiliğinden siler: handler önbelleği (<c>ICachableRequest</c>) ve HTTP önbelleği (<c>WithResponseCache</c>) bu
    /// etiketi taşıyorsa, o tipten bir kayıt eklendiğinde, değiştiğinde ya da silindiğinde eski yanıt dönmez.
    /// Transaction içindeyse silme commit'ten sonra yapılır.
    /// </summary>
    /// <remarks>
    /// Varsayılan kapalıdır: her kayıtta değişen tip başına bir silme yapılır (Redis kullanılıyorsa bir ağ çağrısı).
    /// <c>ICacheRemoverRequest.CacheTagsToRemove</c> ile elle silme her durumda çalışır.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddCanPersistence&lt;AppDbContext&gt;(o =&gt; o.UseNpgsql(connectionString));
    /// services.AddCanEntityCacheInvalidation();
    ///
    /// // sorgu: public IReadOnlyCollection&lt;string&gt; CacheTags =&gt; [CanFx.Application.CacheTags.Entity&lt;Product&gt;()];
    /// // uç nokta: app.MapGet("/products", ...).WithResponseCache(ResponseCacheScope.Tenant, CacheTags.Entity&lt;Product&gt;());
    /// </code>
    /// </example>
    public static IServiceCollection AddCanEntityCacheInvalidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<EntityCacheInvalidationState>();
        services.TryAddScoped<EntityCacheInvalidationInterceptor>();
        services.TryAddScoped<EntityCacheInvalidationTransactionInterceptor>();
        return services;
    }

    /// <summary>
    /// Audit trail ayarları. Tablonun modele eklenmesi gerekir: <c>modelBuilder.AddCanAuditTrail()</c>.
    /// </summary>
    public static IServiceCollection AddCanAuditTrail(this IServiceCollection services, Action<AuditTrailOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AuditTrailOptions();
        configure?.Invoke(options);

        services.RemoveAll<AuditTrailOptions>();
        services.AddSingleton(options);
        return services;
    }

    /// <summary>
    /// Outbox işlemcisini tekrarlayan arka plan işi olarak kaydeder. Tablonun modele eklenmesi gerekir:
    /// <c>modelBuilder.AddCanOutbox()</c>.
    /// </summary>
    public static IServiceCollection AddCanOutbox<TContext>(this IServiceCollection services, Action<OutboxOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new OutboxOptions();
        configure?.Invoke(options);

        services.RemoveAll<OutboxOptions>();
        services.AddSingleton(options);
        services.AddCanRecurringJob<OutboxProcessor<TContext>>(o =>
        {
            o.Interval = options.Interval;
            o.RunOnStartup = true;
        });

        return services;
    }

    /// <summary>
    /// Kalıcı inbox: broker'dan gelen her event bir tüketicide yalnızca bir kez işlenir; handler'ların
    /// <typeparamref name="TContext"/> değişiklikleri ve inbox kaydı aynı transaction'dadır. Tablo:
    /// <c>modelBuilder.AddCanInbox()</c>. Eski kayıtlar düzenli silinir.
    /// </summary>
    public static IServiceCollection AddCanInbox<TContext>(this IServiceCollection services, Action<InboxOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new InboxOptions();
        configure?.Invoke(options);

        services.RemoveAll<InboxOptions>();
        services.AddSingleton(options);
        services.RemoveAll<IInboxStore>();
        services.AddSingleton<IInboxStore, EfInboxStore<TContext>>();
        services.AddCanRecurringJob<InboxCleanupJob<TContext>>(o =>
        {
            o.Interval = options.CleanupInterval;
            o.RunOnStartup = false;
            o.PerTenant = options.PerTenantDatabases;
        });

        return services;
    }
}
