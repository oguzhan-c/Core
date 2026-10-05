using Can.Core.Domain.Entities;

namespace Can.Core.Domain.Auditing;

// Kolaylık sınıfları. İhtiyacına göre seç:
//
//   Entity<TId>                       → sadece kimlik
//   AuditedEntity<TId>                → + Created/Updated
//   FullAuditedEntity<TId>            → + soft delete
//   AggregateRoot<TId>                → kimlik + domain event
//   AuditedAggregateRoot<TId>         → + Created/Updated
//   FullAuditedAggregateRoot<TId>     → + soft delete

/// <summary>Kimlik + oluşturma/güncelleme bilgisi.</summary>
public abstract class AuditedEntity<TId> : Entity<TId>, IAudited
    where TId : notnull, IEquatable<TId>
{
    protected AuditedEntity() { }

    protected AuditedEntity(TId id)
        : base(id) { }

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Kimlik + oluşturma/güncelleme + soft delete bilgisi.</summary>
public abstract class FullAuditedEntity<TId> : AuditedEntity<TId>, IFullAudited
    where TId : notnull, IEquatable<TId>
{
    protected FullAuditedEntity() { }

    protected FullAuditedEntity(TId id)
        : base(id) { }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

/// <summary>Aggregate root + oluşturma/güncelleme bilgisi.</summary>
public abstract class AuditedAggregateRoot<TId> : AggregateRoot<TId>, IAudited
    where TId : notnull, IEquatable<TId>
{
    protected AuditedAggregateRoot() { }

    protected AuditedAggregateRoot(TId id)
        : base(id) { }

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>Aggregate root + oluşturma/güncelleme + soft delete bilgisi.</summary>
public abstract class FullAuditedAggregateRoot<TId> : AuditedAggregateRoot<TId>, IFullAudited
    where TId : notnull, IEquatable<TId>
{
    protected FullAuditedAggregateRoot() { }

    protected FullAuditedAggregateRoot(TId id)
        : base(id) { }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
