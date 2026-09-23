namespace Domain.Entities;

public abstract class Entity<TId> : IEntity<TId> , IEntityTimestamps
{
    public required TId Id { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public DateTimeOffset? UpdatedDate { get; set; }
    public DateTimeOffset? DeletedDate { get; set; }
}