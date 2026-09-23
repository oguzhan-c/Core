namespace Domain.Entities;

public interface IEntityTimestamps
{
    DateTimeOffset CreatedDate { get; set; }
    DateTimeOffset? UpdatedDate { get; set; }
    DateTimeOffset? DeletedDate { get; set; }
}