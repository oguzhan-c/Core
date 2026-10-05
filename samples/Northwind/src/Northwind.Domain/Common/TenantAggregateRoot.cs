using Can.Core.Domain.Auditing;
using Can.Core.Domain.MultiTenancy;

namespace Northwind.Domain.Common;

/// <summary>
/// Bir mağazaya (tenant) ait aggregate: kimlik Guid v7, oluşturma/güncelleme bilgisi, soft delete ve TenantId.
/// TenantId kayıt sırasında aktif tenant'tan otomatik doldurulur; sorgular yalnızca aktif tenant'ın kayıtlarını görür.
/// </summary>
public abstract class TenantAggregateRoot : FullAuditedAggregateRoot<Guid>, IMultiTenant<Guid>
{
    protected TenantAggregateRoot() { }

    protected TenantAggregateRoot(Guid id)
        : base(id) { }

    public Guid TenantId { get; set; }
}
