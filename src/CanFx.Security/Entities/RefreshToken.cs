using CanFx.Domain.Auditing;
using CanFx.Domain.Entities;
using CanFx.Security.Tokens;

namespace CanFx.Security.Entities;

/// <summary>
/// Refresh token kaydı. Token'ın kendisi değil SHA-256 hash'i saklanır.
/// </summary>
/// <remarks>
/// Önerilen akış (rotasyon):
/// <list type="number">
/// <item>İstemci refresh token gönderir → <c>ITokenService.HashRefreshToken</c> ile hash'lenip aranır.</item>
/// <item>Kayıt aktifse (<see cref="IsActive"/>): yeni token üretilir, eskisi
/// <see cref="Revoke"/> ile "yenisiyle değiştirildi" olarak kapatılır.</item>
/// <item>Kayıt bulunur ama zaten iptal edilmişse token çalınmış olabilir: kullanıcının TÜM aktif
/// refresh token'larını iptal et (yeniden kullanım tespiti).</item>
/// <item>Kullanıcının güvenlik damgası token oluşturulduğundan beri değiştiyse (şifre/2FA değişti) token geçersizdir:
/// <see cref="IsValidFor"/>.</item>
/// </list>
/// <para>
/// <b>Oturum:</b> Girişte oluşan token yeni bir oturum (<see cref="SessionId"/>) başlatır; <see cref="Rotate"/> ile
/// üretilen her yeni token aynı oturumu sürdürür. Kullanıcının "aktif oturumlarım" listesi ve tek bir cihazdan çıkış
/// bu kimlikle yapılır (<c>IUserSessionService</c>). Access token'a da <c>TokenSubject.SessionId</c> ile yazılır.
/// </para>
/// </remarks>
public class RefreshToken<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected RefreshToken()
    {
        UserId = default!;
        TokenHash = string.Empty;
    }

    /// <param name="userId">Kullanıcı.</param>
    /// <param name="token">Yeni token (<c>ITokenService.CreateRefreshToken</c>).</param>
    /// <param name="createdAt">Oluşturma anı.</param>
    /// <param name="createdByIp">İstemcinin IP'si.</param>
    /// <param name="securityStamp">Kullanıcının o anki güvenlik damgası; değişirse token geçersiz olur.</param>
    public RefreshToken(TId userId, RefreshTokenValue token, DateTimeOffset createdAt, string? createdByIp, string? securityStamp = null)
        : this(userId, token, createdAt, createdByIp, securityStamp, device: null)
    {
    }

    /// <summary>Girişte: yeni bir oturum başlatır.</summary>
    /// <param name="userId">Kullanıcı.</param>
    /// <param name="token">Yeni token (<c>ITokenService.CreateRefreshToken</c>).</param>
    /// <param name="createdAt">Oluşturma anı.</param>
    /// <param name="createdByIp">İstemcinin IP'si.</param>
    /// <param name="securityStamp">Kullanıcının o anki güvenlik damgası; değişirse token geçersiz olur.</param>
    /// <param name="device">Oturumun cihazı ("aktif oturumlar" listesinde görünür).</param>
    public RefreshToken(TId userId, RefreshTokenValue token, DateTimeOffset createdAt, string? createdByIp, string? securityStamp, SessionDevice? device)
    {
        ArgumentNullException.ThrowIfNull(token);

        UserId = userId;
        TokenHash = token.TokenHash;
        ExpiresAt = token.ExpiresAt;
        CreatedAt = createdAt;
        CreatedByIp = Limit(createdByIp, IpMaxLength);
        SecurityStamp = securityStamp;
        SessionId = Guid.NewGuid().ToString("N");
        SessionStartedAt = createdAt;
        DeviceId = Limit(device?.DeviceId, SessionDevice.DeviceIdMaxLength);
        DeviceName = Limit(device?.DeviceName, SessionDevice.DeviceNameMaxLength);
        UserAgent = Limit(device?.UserAgent, SessionDevice.UserAgentMaxLength);
        LastUsedAt = createdAt;
        LastUsedIp = CreatedByIp;
    }

    /// <summary>IP sütununun uzunluğu (IPv6 dahil).</summary>
    public const int IpMaxLength = 64;

    public TId UserId { get; protected set; }

    [DisableAuditing]
    public string TokenHash { get; protected set; }

    public DateTimeOffset ExpiresAt { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public string? CreatedByIp { get; protected set; }

    public DateTimeOffset? RevokedAt { get; protected set; }

    public string? RevokedByIp { get; protected set; }

    public string? RevokedReason { get; protected set; }

    /// <summary>Rotasyonda bunun yerine verilen token'ın hash'i (token zincirini izlemek için).</summary>
    [DisableAuditing]
    public string? ReplacedByTokenHash { get; protected set; }

    /// <summary>Oluşturulduğu andaki güvenlik damgası (eski kayıtlarda boş: denetlenmez).</summary>
    [DisableAuditing]
    public string? SecurityStamp { get; protected set; }

    /// <summary>Oturumun kimliği: girişte oluşur, rotasyonda değişmez (32 karakter).</summary>
    public string SessionId { get; protected set; } = string.Empty;

    /// <summary>Oturumun başladığı an (ilk girişin zamanı; rotasyonda korunur).</summary>
    public DateTimeOffset SessionStartedAt { get; protected set; }

    /// <summary>İstemcinin kendi cihaz kimliği (mobil uygulamanın ürettiği; kayıtlı cihazla eşleşir).</summary>
    public string? DeviceId { get; protected set; }

    /// <summary>Kullanıcıya gösterilen cihaz adı ("Ayşe'nin iPhone'u", "Chrome · macOS").</summary>
    public string? DeviceName { get; protected set; }

    public string? UserAgent { get; protected set; }

    /// <summary>Oturumun en son yenilendiği an.</summary>
    public DateTimeOffset? LastUsedAt { get; protected set; }

    public string? LastUsedIp { get; protected set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);

    /// <summary>
    /// Yenilemede kullan: aktif mi ve kullanıcının güvenlik damgası token oluşturulduğundan beri değişmedi mi
    /// (değiştiyse şifre/2FA değişmiştir, eski oturum devam etmemeli).
    /// </summary>
    public bool IsValidFor(DateTimeOffset now, string currentSecurityStamp) =>
        IsActive(now) && (SecurityStamp is null || string.Equals(SecurityStamp, currentSecurityStamp, StringComparison.Ordinal));

    public void Revoke(DateTimeOffset now, string? ip, string reason, string? replacedByTokenHash = null)
    {
        if (IsRevoked)
            return;

        RevokedAt = now;
        RevokedByIp = Limit(ip, IpMaxLength);
        RevokedReason = reason;
        ReplacedByTokenHash = replacedByTokenHash;
    }

    /// <summary>
    /// Yenileme (rotasyon): bu token'ı "yenisiyle değiştirildi" olarak kapatır ve aynı oturumu sürdüren yeni token'ı
    /// döner (oturum kimliği, başlangıcı ve cihaz bilgisi korunur). Yeni kaydı DbContext'e ekle. Önce
    /// <see cref="IsValidFor"/> ile geçerliliğini kontrol et.
    /// </summary>
    /// <param name="next">Yeni token (<c>ITokenService.CreateRefreshToken</c>).</param>
    /// <param name="now">Şimdi.</param>
    /// <param name="ip">İstemcinin IP'si.</param>
    /// <param name="securityStamp">Kullanıcının güncel güvenlik damgası.</param>
    /// <param name="userAgent">Güncel tarayıcı/uygulama bilgisi; boşsa öncekisi kalır.</param>
    public RefreshToken<TId> Rotate(RefreshTokenValue next, DateTimeOffset now, string? ip, string? securityStamp, string? userAgent = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (IsRevoked)
            throw new InvalidOperationException("İptal edilmiş token yenilenemez (yeniden kullanım: kullanıcının oturumlarını kapat).");

        var rotated = new RefreshToken<TId>(UserId, next, now, ip, securityStamp, new SessionDevice(DeviceId, DeviceName, userAgent ?? UserAgent))
        {
            SessionId = SessionId,
            SessionStartedAt = SessionStartedAt,
        };
        Revoke(now, ip, RotatedReason, next.TokenHash);
        return rotated;
    }

    /// <summary><see cref="Rotate"/>'in iptal nedeni.</summary>
    public const string RotatedReason = "rotated";

    private static string? Limit(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

/// <summary>
/// Oturumun cihazı. Değerler istemciden gelir; yalnızca gösterim ve eşleştirme içindir (güvenlik kararı için kayıtlı cihaz
/// doğrulaması: <c>ITrustedDeviceService</c>). Uzun değerler kırpılır.
/// </summary>
/// <param name="DeviceId">Mobil uygulamanın ürettiği kalıcı cihaz kimliği (web için boş bırakılabilir).</param>
/// <param name="DeviceName">Gösterilen ad.</param>
/// <param name="UserAgent">Tarayıcı/uygulama bilgisi (<c>User-Agent</c>).</param>
public sealed record SessionDevice(string? DeviceId = null, string? DeviceName = null, string? UserAgent = null)
{
    public const int DeviceIdMaxLength = 64;
    public const int DeviceNameMaxLength = 128;
    public const int UserAgentMaxLength = 512;
}
