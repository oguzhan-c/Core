using System.Security.Cryptography;
using Can.Core.Domain.Auditing;

namespace Can.Core.Security.Entities;

/// <summary>Girişte şifreden sonra istenecek ikinci adım.</summary>
public enum AuthenticatorType
{
    None = 0,
    Email = 1,
    Otp = 2,
}

/// <summary>
/// Kullanıcı. Projede türetip kendi alanlarını ekle:
/// <code>
/// public sealed class AppUser : User&lt;Guid&gt;
/// {
///     private AppUser() { }
///     public AppUser(string email, string firstName) : base(Guid.CreateVersion7(), email) =&gt; FirstName = firstName;
///     public string FirstName { get; private set; } = "";
/// }
/// </code>
/// </summary>
/// <remarks>
/// Şifre yalnızca hash'lenmiş hâliyle tutulur (<see cref="PasswordHash"/>; salt hash'in içindedir).
/// Sadece passkey ile giriş yapan kullanıcıların şifresi olmayabilir.
/// </remarks>
public class User<TId> : FullAuditedAggregateRoot<TId>
    where TId : notnull, IEquatable<TId>
{
    protected User()
    {
        Email = string.Empty;
        NormalizedEmail = string.Empty;
        SecurityStamp = NewSecurityStamp();
        PasskeyUserHandle = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>Id'yi kendisi üreten kullanıcılar için (ör. <c>Guid.CreateVersion7()</c>).</summary>
    public User(TId id, string email, string? userName = null)
        : base(id)
    {
        Email = string.Empty;
        NormalizedEmail = string.Empty;
        SecurityStamp = NewSecurityStamp();
        PasskeyUserHandle = RandomNumberGenerator.GetBytes(32);
        SetEmail(email);
        UserName = userName;
    }

    public string Email { get; protected set; }

    /// <summary>Büyük harfe çevrilmiş e-posta; benzersiz index ve arama için.</summary>
    public string NormalizedEmail { get; protected set; }

    public string? UserName { get; protected set; }

    public bool EmailConfirmed { get; protected set; }

    [DisableAuditing]
    public string? PasswordHash { get; protected set; }

    /// <summary>
    /// Şifre, e-posta ya da 2FA ayarı değiştiğinde yenilenir. Token'lara/oturumlara yazılırsa eski
    /// oturumları geçersiz kılmak için kullanılabilir.
    /// </summary>
    [DisableAuditing]
    public string SecurityStamp { get; protected set; }

    public AuthenticatorType AuthenticatorType { get; protected set; }

    public int AccessFailedCount { get; protected set; }

    public DateTimeOffset? LockoutEnd { get; protected set; }

    /// <summary>Passkey'lerde kullanılan, kişisel veri içermeyen rastgele kullanıcı kimliği.</summary>
    [DisableAuditing]
    public byte[] PasskeyUserHandle { get; protected set; }

    public ICollection<UserRole<TId>> UserRoles { get; protected set; } = [];

    // ---------------------------------------------------------------- davranışlar

    public void SetEmail(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        email = email.Trim();
        if (string.Equals(email, Email, StringComparison.OrdinalIgnoreCase))
            return;

        Email = email;
        NormalizedEmail = email.ToUpperInvariant();
        EmailConfirmed = false;
        RotateSecurityStamp();
    }

    public void SetUserName(string? userName) => UserName = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();

    public void ConfirmEmail() => EmailConfirmed = true;

    /// <summary>Hash'lenmiş şifreyi ayarlar (<c>IPasswordHasher.Hash</c> ile üret).</summary>
    public void SetPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        RotateSecurityStamp();
    }

    public void SetAuthenticator(AuthenticatorType authenticatorType)
    {
        AuthenticatorType = authenticatorType;
        RotateSecurityStamp();
    }

    public void RotateSecurityStamp() => SecurityStamp = NewSecurityStamp();

    // ---------------------------------------------------------------- hesap kilitleme

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd is { } end && end > now;

    /// <summary>
    /// Hatalı giriş denemesini kaydeder; <paramref name="maxAttempts"/>'e ulaşınca hesabı
    /// <paramref name="lockoutDuration"/> (varsayılan 15 dk) boyunca kilitler.
    /// </summary>
    public void RegisterFailedAccess(DateTimeOffset now, int maxAttempts = 5, TimeSpan? lockoutDuration = null)
    {
        AccessFailedCount++;

        if (AccessFailedCount >= maxAttempts)
        {
            LockoutEnd = now.Add(lockoutDuration ?? TimeSpan.FromMinutes(15));
            AccessFailedCount = 0;
        }
    }

    public void ResetAccessFailed()
    {
        AccessFailedCount = 0;
        LockoutEnd = null;
    }

    private static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
