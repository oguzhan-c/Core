namespace Can.Core.Domain.Events;

/// <summary>
/// Domain event'ler için temel record. Record olduğu için değişmezdir ve değere göre karşılaştırılır.
/// </summary>
/// <example>
/// <code>
/// public sealed record OrderConfirmed(Guid OrderId, decimal Total) : DomainEvent;
/// </code>
/// </example>
public abstract record DomainEvent : IDomainEvent
{
    /// <inheritdoc />
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    /// <inheritdoc />
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
