namespace Domain.Events;

public interface IDomainEvent<out TId>
{
    TId EventId { get; }
    DateTimeOffset OccurredAt { get;}
}