namespace Can.Core.Persistence.AuditTrail;

public enum AuditAction
{
    Created = 1,
    Updated = 2,

    /// <summary>Kalıcı silme ya da soft delete (soft delete'te <c>Changes</c> içinde <c>IsDeleted</c> görünür).</summary>
    Deleted = 3,
}

/// <summary>Bir entity'deki tek bir değişikliğin kaydı: kim, ne zaman, hangi alanları neyden neye değiştirdi.</summary>
/// <remarks>
/// Tenant filtresi UYGULANMAZ (yönetim ekranları için); tenant'a göre listelerken <see cref="TenantId"/> ile filtrele.
/// </remarks>
public sealed class AuditLog
{
    private AuditLog()
    {
        EntityType = string.Empty;
        EntityId = string.Empty;
    }

    internal AuditLog(string entityType, string entityId, AuditAction action, string? changes, string? userId, string? tenantId, DateTimeOffset timestamp, string? traceId)
    {
        Id = Guid.CreateVersion7();
        EntityType = entityType;
        EntityId = entityId;
        Action = action;
        Changes = changes;
        UserId = userId;
        TenantId = tenantId;
        Timestamp = timestamp;
        TraceId = traceId;
    }

    public Guid Id { get; private set; }

    /// <summary>Entity tipinin adı (ör. <c>Product</c>).</summary>
    public string EntityType { get; private set; }

    /// <summary>Kaydın birincil anahtarı (bileşik anahtarlarda virgülle ayrılmış).</summary>
    public string EntityId { get; internal set; }

    public AuditAction Action { get; private set; }

    /// <summary>
    /// Değişen alanlar, JSON: <c>{"Price":{"old":10,"new":12}}</c>. Oluşturmada yalnızca <c>new</c>,
    /// kalıcı silmede yalnızca <c>old</c> doludur.
    /// </summary>
    public string? Changes { get; private set; }

    public string? UserId { get; private set; }

    public string? TenantId { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }

    /// <summary>İsteğin trace kimliği; aynı istekteki değişiklikleri ve logları ilişkilendirmek için.</summary>
    public string? TraceId { get; private set; }
}
