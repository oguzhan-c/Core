namespace Can.Core.Domain.Events;

/// <summary>
/// Domain'de gerçekleşmiş, iş açısından anlamlı bir olay (ör. <c>OrderConfirmed</c>).
/// </summary>
/// <remarks>
/// Bu arayüz hiçbir mediator kütüphanesine bağımlı değildir. Can.Core.Mediator, handler'ları
/// <c>INotificationHandler&lt;TEvent&gt;</c> ile doğrudan bu tiplere bağlayabilir; ayrıca bir
/// dispatcher yazmaya gerek kalmaz.
/// </remarks>
public interface IDomainEvent
{
    /// <summary>Olayın benzersiz kimliği (idempotency, outbox vb. için).</summary>
    Guid EventId { get; }

    /// <summary>Olayın gerçekleştiği an (UTC).</summary>
    DateTimeOffset OccurredAt { get; }
}
