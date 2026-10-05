using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Mediator.DependencyInjection;

public static class ServiceCollectionExtensions
{
    private static readonly Type[] HandlerInterfaces =
    [
        typeof(IRequestHandler<,>),
        typeof(IStreamRequestHandler<,>),
        typeof(INotificationHandler<>),
    ];

    /// <summary>
    /// Mediator'ı, verilen assembly'lerdeki handler'ları ve behavior'ları kaydeder.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddCanMediator(cfg =>
    /// {
    ///     cfg.RegisterServicesFromAssemblyContaining&lt;CreateOrderCommand&gt;();
    ///     cfg.AddOpenBehavior(typeof(ValidationBehavior&lt;,&gt;));
    ///     cfg.AddOpenBehavior(typeof(TransactionBehavior&lt;,&gt;));
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanMediator(
        this IServiceCollection services,
        Action<MediatorConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var configuration = new MediatorConfiguration();
        configure(configuration);

        if (!typeof(INotificationPublisher).IsAssignableFrom(configuration.NotificationPublisherType))
        {
            throw new ArgumentException(
                $"'{configuration.NotificationPublisherType.FullName}' INotificationPublisher uygulamalı."
            );
        }

        services.TryAdd(new ServiceDescriptor(typeof(IMediator), typeof(Mediator), configuration.Lifetime));
        services.TryAdd(
            new ServiceDescriptor(typeof(ISender), sp => sp.GetRequiredService<IMediator>(), configuration.Lifetime)
        );
        services.TryAdd(
            new ServiceDescriptor(typeof(IPublisher), sp => sp.GetRequiredService<IMediator>(), configuration.Lifetime)
        );
        services.TryAddSingleton(typeof(INotificationPublisher), configuration.NotificationPublisherType);

        foreach (Assembly assembly in configuration.Assemblies)
            RegisterHandlers(services, assembly, configuration.Lifetime);

        // Behavior'lar eklenme sırasıyla kaydedilir; Mediator bu sırayı korur.
        foreach ((Type serviceType, Type implementationType) in configuration.Behaviors)
            services.TryAddEnumerable(new ServiceDescriptor(serviceType, implementationType, configuration.BehaviorLifetime));

        foreach ((Type serviceType, Type implementationType) in configuration.StreamBehaviors)
            services.TryAddEnumerable(new ServiceDescriptor(serviceType, implementationType, configuration.BehaviorLifetime));

        return services;
    }

    private static void RegisterHandlers(IServiceCollection services, Assembly assembly, ServiceLifetime lifetime)
    {
        foreach (Type type in GetLoadableTypes(assembly))
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
                continue;

            foreach (Type @interface in type.GetInterfaces())
            {
                if (!@interface.IsGenericType)
                    continue;

                Type definition = @interface.GetGenericTypeDefinition();
                if (Array.IndexOf(HandlerInterfaces, definition) < 0)
                    continue;

                // TryAddEnumerable: aynı assembly iki kez taransa da handler bir kez kaydedilir.
                services.TryAddEnumerable(new ServiceDescriptor(@interface, type, lifetime));
            }
        }
    }

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
