using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;
using Can.Core.Security.Passkeys;
using Can.Core.Security.VerificationCodes;

namespace Can.Core.Security.Entities;

/// <summary>
/// Kullanıcının authenticator uygulaması (TOTP) kaydı.
/// </summary>
/// <remarks>
/// <see cref="SecretKey"/> ile kodlar üretilebildiği için veritabanında ŞİFRELİ saklanması önerilir
/// (ör. ASP.NET Data Protection ile bir EF value converter).
/// </remarks>
public class OtpAuthenticator<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected OtpAuthenticator()
    {
        UserId = default!;
        SecretKey = [];
    }

    public OtpAuthenticator(TId userId, byte[] secretKey)
    {
        ArgumentNullException.ThrowIfNull(secretKey);
        UserId = userId;
        SecretKey = secretKey;
    }

    public TId UserId { get; protected set; }

    [DisableAuditing]
    public byte[] SecretKey { get; protected set; }

    /// <summary>Kullanıcı ilk kodu girip kurulumu tamamladı mı?</summary>
    public bool IsVerified { get; protected set; }

    /// <summary>Son kabul edilen kodun zaman adımı; aynı kod ikinci kez kullanılamaz.</summary>
    public long? LastUsedTimeStep { get; protected set; }

    /// <summary><c>ITotpService.TryVerify</c> başarılı olduğunda çağır.</summary>
    public void MarkCodeUsed(long timeStep)
    {
        LastUsedTimeStep = timeStep;
        IsVerified = true;
    }
}

/// <summary>E-posta ile gönderilen tek kullanımlık kod kaydı (ikinci adım doğrulama, e-posta onayı ...).</summary>
public class EmailAuthenticator<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    public const int DefaultMaxAttempts = 5;

    protected EmailAuthenticator()
    {
        UserId = default!;
    }

    public EmailAuthenticator(TId userId)
    {
        UserId = userId;
    }

    public TId UserId { get; protected set; }

    [DisableAuditing]
    public string? CodeHash { get; protected set; }

    public DateTimeOffset? ExpiresAt { get; protected set; }

    public int FailedAttempts { get; protected set; }

    public bool IsVerified { get; protected set; }

    /// <summary>Yeni kod gönderildiğinde: önceki kod geçersiz olur, deneme sayacı sıfırlanır.</summary>
    public void SetCode(VerificationCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        CodeHash = code.CodeHash;
        ExpiresAt = code.ExpiresAt;
        FailedAttempts = 0;
    }

    /// <summary>Kod hâlâ denenebilir mi (süresi dolmamış ve deneme sınırı aşılmamış)?</summary>
    public bool CanAttempt(DateTimeOffset now, int maxAttempts = DefaultMaxAttempts) =>
        CodeHash is not null && ExpiresAt > now && FailedAttempts < maxAttempts;

    public void RegisterFailedAttempt() => FailedAttempts++;

    /// <summary>Doğru kod girildiğinde: kod tekrar kullanılamasın diye silinir.</summary>
    public void MarkVerified()
    {
        IsVerified = true;
        CodeHash = null;
        ExpiresAt = null;
        FailedAttempts = 0;
    }
}

/// <summary>Kullanıcının kayıtlı passkey'i (bir kullanıcının birden fazla cihazı olabilir).</summary>
public class UserPasskey<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected UserPasskey()
    {
        UserId = default!;
        CredentialId = [];
        PublicKey = [];
        UserHandle = [];
        Name = string.Empty;
    }

    public UserPasskey(TId userId, PasskeyCredential credential, string name, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(credential);

        UserId = userId;
        CredentialId = credential.CredentialId;
        PublicKey = credential.PublicKey;
        UserHandle = credential.UserHandle;
        SignCount = credential.SignCount;
        AaGuid = credential.AaGuid;
        Transports = credential.Transports;
        IsBackedUp = credential.IsBackedUp;
        Name = string.IsNullOrWhiteSpace(name) ? "Passkey" : name.Trim();
        CreatedAt = createdAt;
    }

    public TId UserId { get; protected set; }

    /// <summary>Benzersiz index konulmalı; girişte passkey bununla bulunur.</summary>
    public byte[] CredentialId { get; protected set; }

    [DisableAuditing]
    public byte[] PublicKey { get; protected set; }

    public byte[] UserHandle { get; protected set; }

    public uint SignCount { get; protected set; }

    /// <summary>Authenticator modeli (ör. iCloud Keychain, Windows Hello, YubiKey).</summary>
    public Guid AaGuid { get; protected set; }

    public string? Transports { get; protected set; }

    /// <summary>Passkey bulutla senkronize ediliyor mu (iCloud, Google Password Manager ...).</summary>
    public bool IsBackedUp { get; protected set; }

    /// <summary>Kullanıcının verdiği ad, ör. "iPhone", "İş bilgisayarı".</summary>
    public string Name { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset? LastUsedAt { get; protected set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
    }

    /// <summary>Başarılı girişten sonra çağır (<c>IPasskeyService.CompleteLoginAsync</c> sonucu ile).</summary>
    public void RecordUse(uint signCount, DateTimeOffset now)
    {
        SignCount = signCount;
        LastUsedAt = now;
    }
}
