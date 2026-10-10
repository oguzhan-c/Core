using Can.Core.Persistence.AuditTrail;
using Northwind.Domain.Identity;

namespace Northwind.Application.Common;

/// <summary>
/// Tenant filtresinin dışına çıkması gereken kimlik sorguları (Infrastructure uygular). Örneğin refresh token ile
/// gelen istekte henüz tenant bilinmez; kullanıcı önce bulunur, tenant'ı ondan belirlenir.
/// </summary>
public interface IIdentityStore
{
    /// <summary>Kullanıcıyı rolleriyle birlikte, tenant'ından bağımsız olarak getirir (silinmişler hariç).</summary>
    Task<AppUser?> FindUserInAnyTenantAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Denetim kayıtlarına salt-okunur erişim.</summary>
public interface IAuditLogReader
{
    IQueryable<AuditLog> Query();
}

/// <summary>
/// Outbox tablosuna salt-okunur erişim ve mesajı yeniden kuyruğa alma (yönetim paneli için).
/// </summary>
public interface IOutboxMonitor
{
    IQueryable<Can.Core.Persistence.Outbox.OutboxMessage> Query();

    /// <summary>
    /// Yayınlanmamış (bekleyen ya da ölü) mesajı yeniden kuyruğa alır; bir sonraki turda yayınlanır.
    /// </summary>
    /// <param name="id">Mesajın kimliği.</param>
    /// <param name="tenantId">Mesajın ait olması gereken tenant (başka mağazanın mesajına dokunulmaz).</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    /// <returns>Mesaj bulunup kuyruğa alındıysa <see langword="true"/>.</returns>
    Task<bool> RetryAsync(Guid id, string? tenantId, CancellationToken cancellationToken);
}

/// <summary>Bildirim ayarları (appsettings: <c>Notifications</c>).</summary>
public sealed class NotificationOptions
{
    /// <summary>Sipariş/stok bildirimlerinin gideceği adres. Boşsa bildirimler yalnızca loglanır.</summary>
    public string? OperationsEmail { get; set; }
}
