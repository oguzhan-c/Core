using System.Reflection;
using Can.Core.Mediator.Publishers;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Mediator.DependencyInjection;

/// <summary><c>AddCanMediator(cfg =&gt; ...)</c> ayarları.</summary>
public sealed class MediatorConfiguration
{
    internal List<Assembly> Assemblies { get; } = [];

    internal List<(Type ServiceType, Type ImplementationType)> Behaviors { get; } = [];

    internal List<(Type ServiceType, Type ImplementationType)> StreamBehaviors { get; } = [];

    /// <summary>Handler'ların ve <see cref="IMediator"/>'ın DI ömrü. Varsayılan: Transient.</summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>Behavior'ların DI ömrü. Varsayılan: Transient.</summary>
    public ServiceLifetime BehaviorLifetime { get; set; } = ServiceLifetime.Transient;

    /// <summary>
    /// Bildirim yayın stratejisinin tipi. Varsayılan: <see cref="ForeachAwaitPublisher"/>
    /// (sırayla). Alternatif: <see cref="TaskWhenAllPublisher"/> (paralel) ya da kendi
    /// <see cref="INotificationPublisher"/> implementasyonun.
    /// </summary>
    public Type NotificationPublisherType { get; set; } = typeof(ForeachAwaitPublisher);

    /// <summary>Handler'ları (request, stream, notification) bu assembly'lerden tara.</summary>
    public MediatorConfiguration RegisterServicesFromAssemblies(params Assembly[] assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            if (!Assemblies.Contains(assembly))
                Assemblies.Add(assembly);
        }

        return this;
    }

    /// <summary>Handler'ları <typeparamref name="T"/> tipinin bulunduğu assembly'den tara.</summary>
    public MediatorConfiguration RegisterServicesFromAssemblyContaining<T>() =>
        RegisterServicesFromAssemblies(typeof(T).Assembly);

    /// <summary>
    /// Açık generic bir pipeline behavior ekler, ör. <c>AddOpenBehavior(typeof(LoggingBehavior&lt;,&gt;))</c>.
    /// Ekleme sırası çalışma sırasıdır: ilk eklenen en dışta çalışır.
    /// </summary>
    public MediatorConfiguration AddOpenBehavior(Type openBehaviorType)
    {
        EnsureOpenGenericImplementing(openBehaviorType, typeof(IPipelineBehavior<,>));
        Behaviors.Add((typeof(IPipelineBehavior<,>), openBehaviorType));
        return this;
    }

    /// <summary>Kapalı bir behavior ekler (tek bir istek tipine özel).</summary>
    public MediatorConfiguration AddBehavior<TRequest, TResponse, TBehavior>()
        where TRequest : notnull
        where TBehavior : class, IPipelineBehavior<TRequest, TResponse>
    {
        Behaviors.Add((typeof(IPipelineBehavior<TRequest, TResponse>), typeof(TBehavior)));
        return this;
    }

    /// <summary>Açık generic bir stream behavior ekler.</summary>
    public MediatorConfiguration AddOpenStreamBehavior(Type openBehaviorType)
    {
        EnsureOpenGenericImplementing(openBehaviorType, typeof(IStreamPipelineBehavior<,>));
        StreamBehaviors.Add((typeof(IStreamPipelineBehavior<,>), openBehaviorType));
        return this;
    }

    private static void EnsureOpenGenericImplementing(Type type, Type openInterface)
    {
        bool implements =
            type.IsGenericTypeDefinition
            && type.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openInterface);

        if (!implements)
        {
            throw new ArgumentException(
                $"'{type.FullName}' açık generic bir tip olmalı ve '{openInterface.Name}' uygulamalı.",
                nameof(type)
            );
        }
    }
}
