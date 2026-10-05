namespace Can.Core.Domain.MultiTenancy;

/// <summary>
/// Bir tenant'a ait olan entity. Persistence katmanı yeni kayıtlara tenant'ı otomatik atar
/// ve global query filter ile yalnızca aktif tenant'ın kayıtlarını getirir.
/// </summary>
/// <typeparam name="TTenantId">Tenant Id tipi (Guid, int, string ...).</typeparam>
public interface IMultiTenant<TTenantId>
    where TTenantId : notnull
{
    TTenantId TenantId { get; set; }
}
