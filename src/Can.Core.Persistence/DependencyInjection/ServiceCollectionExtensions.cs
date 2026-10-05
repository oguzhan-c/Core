using Can.Core.Application;
using Can.Core.BackgroundJobs;
using Can.Core.Persistence.AuditTrail;
using Can.Core.Persistence.Interceptors;
using Can.Core.Persistence.Outbox;
using Can.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Persistence.DependencyInjection;

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
            }
        );

        services.TryAddScoped<DbContext>(serviceProvider => serviceProvider.GetRequiredService<TContext>());
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));

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
}
