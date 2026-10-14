namespace Can.Core.Security.Sessions;

/// <summary>Kullanıcının açık bir oturumu (bir cihaz/tarayıcı).</summary>
/// <param name="SessionId">Oturum kimliği.</param>
/// <param name="DeviceId">İstemcinin cihaz kimliği (mobil).</param>
/// <param name="DeviceName">Gösterilen cihaz adı.</param>
/// <param name="UserAgent">Tarayıcı/uygulama bilgisi.</param>
/// <param name="IpAddress">Son kullanıldığı IP.</param>
/// <param name="StartedAt">Girişin zamanı.</param>
/// <param name="LastUsedAt">Son yenileme.</param>
/// <param name="ExpiresAt">Yenilenmezse kapanacağı an.</param>
/// <param name="IsCurrent">İsteği yapan oturum mu ("bu cihaz").</param>
public sealed record UserSession(
    string SessionId,
    string? DeviceId,
    string? DeviceName,
    string? UserAgent,
    string? IpAddress,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

/// <summary>
/// "Aktif oturumlarım": listeleme ve kapatma. Kapatılan oturumun refresh token'ı iptal edilir; access token'ı da
/// (<c>IAccessTokenDenylist</c> kayıtlıysa, <c>sid</c> ile) hemen geçersiz olur. Uygulama:
/// <c>Can.Core.Security.EntityFrameworkCore</c> → <c>AddCanUserSessions&lt;TContext, TId&gt;()</c>.
/// </summary>
public interface IUserSessionService
{
    /// <summary>Açık oturumlar, son kullanılan önce.</summary>
    /// <param name="userId">Kullanıcı.</param>
    /// <param name="currentSessionId">İsteği yapan oturum (<c>sid</c> claim'i); <see cref="UserSession.IsCurrent"/> için.</param>
    /// <param name="cancellationToken">İptal.</param>
    Task<IReadOnlyList<UserSession>> ListAsync(string userId, string? currentSessionId = null, CancellationToken cancellationToken = default);

    /// <summary>Tek bir oturumu kapatır ("bu cihazdan çıkış yap"). Oturum yoksa ya da başkasınınsa <see langword="false"/>.</summary>
    Task<bool> RevokeAsync(string userId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Bu oturum dışındakileri kapatır ("diğer cihazlardan çıkış yap"). Kapatılan oturum sayısını döner.</summary>
    Task<int> RevokeOthersAsync(string userId, string currentSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tüm oturumları kapatır. Hesap ele geçirildiyse ayrıca güvenlik damgasını yenile (şifre değişimi bunu zaten yapar).
    /// </summary>
    Task<int> RevokeAllAsync(string userId, CancellationToken cancellationToken = default);
}
