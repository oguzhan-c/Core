namespace Domain.Entities;

public abstract class Entity<TId> : IEntity<TId> , IEntityTimestamps
{
    public required TId Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}