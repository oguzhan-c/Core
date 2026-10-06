using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.EventBus;

public static class EventBusServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="IEventBus"/>'ı kaydeder. Verilen assembly'lerdeki <c>IIntegrationEvent</c>'ler adlarıyla tanınır
    /// (gelen zarflar yalnızca bunlara çevrilir). Taşıyıcı kayıtlı değilse bellek içi taşıyıcı kullanılır; outbox
    /// kayıtlıysa event'leri bus üzerinden yayınlar.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanEventBus(typeof(OrderShipped).Assembly);
    /// // dinleyen taraf: sıradan bir handler
    /// public sealed class OrderShippedHandler : INotificationHandler&lt;OrderShipped&gt; { ... }
    /// </code>
    /// </example>
    public static IServiceCollection AddCanEventBus(this IServiceCollection services, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        // Birden fazla çağrı (ör. modül başına) tipleri birleştirir.
        services.AddSingleton(new EventTypeSource(EventTypeRegistry.Scan(assemblies).ToArray()));
        services.TryAddSingleton(sp => new EventTypeRegistry(sp.GetServices<EventTypeSource>().SelectMany(s => s.Types)));
        services.TryAddSingleton<EventDispatcher>();
        services.TryAddSingleton<IEventTransport, InMemoryEventTransport>();
        services.TryAddScoped<IEventBus, DefaultEventBus>();

        return services;
    }

    /// <summary>Taşıyıcıyı değiştirir (RabbitMQ vb. paketler bunu kullanır).</summary>
    public static IServiceCollection AddCanEventTransport<TTransport>(this IServiceCollection services)
        where TTransport : class, IEventTransport
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IEventTransport>();
        services.AddSingleton<IEventTransport, TTransport>();
        return services;
    }
}

internal sealed record EventTypeSource(IReadOnlyList<Type> Types);
