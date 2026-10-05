using Can.Core.Application;
using Can.Core.Persistence.Interceptors;
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

        services.TryAddScoped<AuditingInterceptor>();
        services.TryAddScoped<DomainEventInterceptor>();

        services.AddDbContext<TContext>(
            (serviceProvider, options) =>
            {
                configure(serviceProvider, options);
                options.AddInterceptors(
                    serviceProvider.GetRequiredService<AuditingInterceptor>(),
                    serviceProvider.GetRequiredService<DomainEventInterceptor>()
                );
            }
        );

        services.TryAddScoped<DbContext>(serviceProvider => serviceProvider.GetRequiredService<TContext>());
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();
        services.TryAddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));

        return services;
    }
}
