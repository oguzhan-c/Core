using System.ComponentModel.DataAnnotations.Schema;
using Can.Core.Domain.Events;

namespace Can.Core.Domain.Entities;

/// <summary>
/// Aggregate root: tutarlılık sınırının sahibi ve domain event'lerin kaynağı.
/// </summary>
/// <remarks>
/// Event'ler yalnızca aggregate'in kendi metotlarından <see cref="RaiseDomainEvent"/> ile eklenir;
/// handler'lar ya da servisler dışarıdan event ekleyemez.
/// <code>
/// public void Confirm()
/// {
///     if (Status != OrderStatus.Draft) throw new BusinessException("...");
///     Status = OrderStatus.Confirmed;
///     RaiseDomainEvent(new OrderConfirmed(Id, Total));
/// }
/// </code>
/// </remarks>
public abstract class AggregateRoot<TId> : Entity<TId>, IHasDomainEvents
    where TId : notnull, IEquatable<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot() { }

    protected AggregateRoot(TId id)
        : base(id) { }

    /// <inheritdoc />
    [NotMapped] // veritabanına yazılmaz (BCL attribute'u, EF bağımlılığı yok)
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <inheritdoc />
    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }
}
