using Domain.Entities;
using Domain.Events;

namespace Domain.Aggregates;

public abstract class AggregateRoot<TId> : Entity<TId>
{
    private readonly List<IDomainEvent<TId>> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent<TId>> DomainEvents =>
        _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(
        IDomainEvent<TId> domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }
}