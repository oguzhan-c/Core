using Can.Core.Domain.Events;

namespace Can.Core.EventBus;

/// <summary>
/// Integration event'leri yayınlar. Event, taşıyıcıya (bellek içi, RabbitMQ ...) bir <see cref="EventEnvelope"/> olarak
/// gider; karşı tarafta mevcut <c>INotificationHandler&lt;TEvent&gt;</c>'lar event'in tenant'ı adına çalışır.
/// </summary>
/// <remarks>
/// Doğrudan <c>PublishAsync</c> veritabanı kaydıyla atomik DEĞİLDİR: kayıt başarısız olsa da event gitmiş olabilir.
/// Veri değişikliğine bağlı event'leri aggregate'ten yükselt (<c>IIntegrationEvent</c>); outbox aynı transaction'da
/// saklar ve bus üzerinden yayınlar.
/// </remarks>
public interface IEventBus
{
    Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default);
}

/// <summary>Zarfı hedefe ulaştırır (bellek içi, RabbitMQ, Azure Service Bus ...).</summary>
public interface IEventTransport
{
    Task SendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default);
}

/// <summary>
/// Taşıyıcı üzerindeki event. Tip, .NET tip adı yerine sabit bir ad ile taşınır; böylece yayınlayan ve dinleyen
/// servisler farklı assembly/namespace'lerde aynı event'i tanımlayabilir.
/// </summary>
/// <param name="EventId">Event'in kimliği; dinleyen taraf tekrarları bununla ayıklayabilir.</param>
/// <param name="EventName">Sabit ad (<see cref="IntegrationEventNameAttribute"/> ya da tipin tam adı).</param>
/// <param name="Payload">Event'in JSON hâli.</param>
/// <param name="TenantId">Event'in oluştuğu tenant; dinleyen taraf bu tenant adına çalışır.</param>
/// <param name="OccurredAt">Event'in oluştuğu an.</param>
public sealed record EventEnvelope(Guid EventId, string EventName, string Payload, string? TenantId, DateTimeOffset OccurredAt)
{
    /// <summary>Ek bilgiler (correlation id, kaynak servis ...).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Event'in taşıyıcıdaki sabit adı. Verilmezse tipin tam adı (<c>Namespace.Tip</c>) kullanılır; sınıfı taşıyınca ya da
/// yeniden adlandırınca dinleyenler bozulmasın diye ad vermek önerilir.
/// </summary>
/// <example><c>[IntegrationEventName("sales.order-shipped")] public sealed record OrderShipped(...) : DomainEvent, IIntegrationEvent;</c></example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventNameAttribute : Attribute
{
    public IntegrationEventNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}
