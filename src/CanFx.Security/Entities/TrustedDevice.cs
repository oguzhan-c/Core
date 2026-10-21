using CanFx.Domain.Auditing;
using CanFx.Domain.Entities;

namespace CanFx.Security.Entities;

/// <summary>
/// Kullanıcının kayıtlı (güvenilen) cihazı: cihazda üretilmiş ECDSA P-256 anahtar çiftinin açık anahtarı. Bkz.
/// <c>IDeviceBindingService</c>.
/// </summary>
public class TrustedDevice<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    public const int DeviceIdMaxLength = SessionDevice.DeviceIdMaxLength;
    public const int NameMaxLength = SessionDevice.DeviceNameMaxLength;
    public const int PlatformMaxLength = 32;

    protected TrustedDevice()
    {
        UserId = default!;
        DeviceId = string.Empty;
        Name = string.Empty;
        PublicKey = [];
    }

    /// <param name="userId">Kullanıcı.</param>
    /// <param name="deviceId">Uygulamanın ürettiği kalıcı cihaz kimliği.</param>
    /// <param name="name">Gösterilen ad.</param>
    /// <param name="platform">ios, android ...</param>
    /// <param name="publicKey">Doğrulanmış açık anahtar (<c>IDeviceBindingService.ParsePublicKey</c>).</param>
    /// <param name="createdAt">Kayıt anı.</param>
    public TrustedDevice(TId userId, string deviceId, string name, string? platform, byte[] publicKey, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(publicKey);
        if (deviceId.Length > DeviceIdMaxLength)
            throw new ArgumentException($"Cihaz kimliği en çok {DeviceIdMaxLength} karakter olabilir.", nameof(deviceId));

        UserId = userId;
        DeviceId = deviceId;
        Name = Clean(name, NameMaxLength) ?? "Cihaz";
        Platform = Clean(platform, PlatformMaxLength);
        PublicKey = publicKey;
        CreatedAt = createdAt;
    }

    public TId UserId { get; protected set; }

    public string DeviceId { get; protected set; }

    public string Name { get; protected set; }

    public string? Platform { get; protected set; }

    [DisableAuditing]
    public byte[] PublicKey { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset? LastUsedAt { get; protected set; }

    /// <summary>En son kabul edilen meydan okumanın üretilme anı (tekrar kullanım engeli).</summary>
    [DisableAuditing]
    public DateTimeOffset? LastChallengeIssuedAt { get; protected set; }

    public DateTimeOffset? RevokedAt { get; protected set; }

    public bool IsActive => RevokedAt is null;

    /// <summary>Uygulama yeniden kuruldu: yeni anahtar, ad; kaldırılmışsa tekrar etkin.</summary>
    public void Replace(string name, string? platform, byte[] publicKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        Name = Clean(name, NameMaxLength) ?? Name;
        Platform = Clean(platform, PlatformMaxLength) ?? Platform;
        PublicKey = publicKey;
        CreatedAt = now;
        LastChallengeIssuedAt = null;
        RevokedAt = null;
    }

    public void MarkVerified(DateTimeOffset challengeIssuedAt, DateTimeOffset now)
    {
        LastChallengeIssuedAt = challengeIssuedAt;
        LastUsedAt = now;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
