namespace Domain.Events;

public abstract record DomainEvent<TId>(TId EventId) : IDomainEvent<TId>
{
    public DateTimeOffset OccurredAt { get; } = DateTimeOffset.Now;
}