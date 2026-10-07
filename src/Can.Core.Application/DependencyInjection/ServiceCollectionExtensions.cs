using System.Reflection;
using Can.Core.Application.Behaviors;
using Can.Core.Application.Rules;
using Can.Core.Mediator.DependencyInjection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Uygulama katmanını tek çağrıyla kurar: verilen assembly'lerdeki handler'lar (mediator),
    /// FluentValidation validator'ları ve business rule sınıfları; pipeline behavior'ları ve HybridCache.
    /// </summary>
    /// <remarks>
    /// Behavior sırası (dıştan içe):
    /// <c>Telemetry → Logging → Performance → Authorization → Validation → Caching → CacheRemoving → Transaction → Handler</c>.
    /// Yetkisiz istekler doğrulama hatalarını görmez; önbellekten yalnızca yetkili ve geçerli isteklere yanıt verilir;
    /// önbellek ancak transaction commit edildikten sonra temizlenir.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddCanApplication(options =&gt; options.AdminRole = "Admin", typeof(CreateProductCommand).Assembly);
    /// </code>
    /// </example>
    public static IServiceCollection AddCanApplication(
        this IServiceCollection services,
        Action<CanApplicationOptions>? configure,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        var options = new CanApplicationOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentUser>(NullCurrentUser.Instance);
        services.TryAddSingleton<ICurrentTenant>(NullCurrentTenant.Instance);
        services.AddHybridCache();

        services.AddCanMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblies(assemblies);

            cfg.AddOpenBehavior(typeof(TelemetryBehavior<,>));
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(PerformanceBehavior<,>));
            cfg.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(CachingBehavior<,>));
            cfg.AddOpenBehavior(typeof(CacheRemovingBehavior<,>));
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });

        services.AddValidatorsFromAssemblies(assemblies, ServiceLifetime.Scoped, includeInternalTypes: true);

        foreach (Type type in assemblies.Distinct().SelectMany(GetLoadableTypes))
        {
            if (type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(BaseBusinessRules).IsAssignableFrom(type))
            {
                services.TryAddScoped(type);
            }
        }

        return services;
    }

    /// <summary>Varsayılan ayarlarla.</summary>
    public static IServiceCollection AddCanApplication(this IServiceCollection services, params Assembly[] assemblies) =>
        services.AddCanApplication(configure: null, assemblies);

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
