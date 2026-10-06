using System.Collections.Frozen;
using System.Reflection;
using Can.Core.Domain.Events;

namespace Can.Core.EventBus;

/// <summary>Event adı ↔ .NET tipi eşlemesi. Yalnızca kayıtlı <see cref="IIntegrationEvent"/> tipleri çözülür.</summary>
public sealed class EventTypeRegistry
{
    private readonly FrozenDictionary<string, Type> _byName;
    private readonly FrozenDictionary<Type, string> _byType;

    public EventTypeRegistry(IEnumerable<Type> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        var byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (Type type in eventTypes.Distinct())
        {
            if (!IsIntegrationEvent(type))
                throw new ArgumentException($"{type.FullName} somut bir IIntegrationEvent değil.", nameof(eventTypes));

            string name = NameOf(type);
            if (byName.TryGetValue(name, out Type? existing) && existing != type)
                throw new InvalidOperationException($"'{name}' event adı hem {existing.FullName} hem {type.FullName} için kullanılmış.");

            byName[name] = type;
        }

        _byName = byName.ToFrozenDictionary(StringComparer.Ordinal);
        _byType = byName.ToFrozenDictionary(p => p.Value, p => p.Key);
    }

    public IReadOnlyCollection<string> Names => _byName.Keys;

    /// <summary>Tipin adı; kayıtlı değilse de ad hesaplanır (yayınlamak için kayıt şart değil).</summary>
    public string GetName(Type eventType) => _byType.TryGetValue(eventType, out string? name) ? name : NameOf(eventType);

    public bool TryGetType(string eventName, out Type? eventType) => _byName.TryGetValue(eventName, out eventType);

    internal static IEnumerable<Type> Scan(IEnumerable<Assembly> assemblies) =>
        assemblies.Distinct().SelectMany(a => a.GetTypes()).Where(t => t.IsVisible && IsIntegrationEvent(t)); // yalnızca public tipler

    private static bool IsIntegrationEvent(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(IIntegrationEvent).IsAssignableFrom(type);

    private static string NameOf(Type type) =>
        type.GetCustomAttribute<IntegrationEventNameAttribute>()?.Name ?? type.FullName ?? type.Name;
}
