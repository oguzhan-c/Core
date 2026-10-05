namespace Can.Core.Persistence.Interceptors;

/// <summary>Interceptor'lar arasında paylaşılan, DbContext scope'una ait durum.</summary>
internal sealed class SaveChangesState
{
    /// <summary>
    /// Audit trail, kalıcı anahtarı kayıttan sonra belli olan kayıtların geçmişini ikinci bir SaveChanges ile yazıyor.
    /// Bu kayıtta yalnızca <c>AuditLog</c>'lar vardır; diğer interceptor'lar (domain event, audit trail) devre dışıdır.
    /// </summary>
    public bool IsWritingAuditTrail { get; set; }
}
