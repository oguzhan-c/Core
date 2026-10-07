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

    /// <summary>Bağlı dış hesaplar (Google, Microsoft, GitHub ...).</summary>
    public ICollection<UserLogin<TId>> Logins { get; protected set; } = [];

    /// <summary>Dış hesabı bağlar; sağlayıcıda zaten bir hesap bağlıysa <see langword="false"/> (önce kaldırılmalı).</summary>
    public bool AddLogin(string loginProvider, string providerKey, string? providerDisplayName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginProvider);
        string provider = loginProvider.Trim().ToLowerInvariant();

        if (Logins.Any(l => l.LoginProvider == provider))
            return false;

        Logins.Add(new UserLogin<TId>(Id, provider, providerKey, providerDisplayName, now));
        RotateSecurityStamp();
        return true;
    }

    /// <summary>Dış hesabın bağlantısını kaldırır.</summary>
    public bool RemoveLogin(string loginProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginProvider);
        string provider = loginProvider.Trim().ToLowerInvariant();

        UserLogin<TId>? login = Logins.FirstOrDefault(l => l.LoginProvider == provider);
        if (login is null)
            return false;

        Logins.Remove(login);
        RotateSecurityStamp();
        return true;
    }

    /// <summary>Rolden bağımsız, doğrudan verilen yetkiler.</summary>
    public ICollection<UserOperationClaim<TId>> OperationClaims { get; protected set; } = [];

    /// <summary>Kullanıcıya doğrudan yetki verir (zaten varsa bir şey yapmaz).</summary>
    public void GrantOperationClaim(OperationClaim<TId> operationClaim)
    {
        ArgumentNullException.ThrowIfNull(operationClaim);
        if (OperationClaims.All(c => !c.OperationClaimId.Equals(operationClaim.Id)))
            OperationClaims.Add(new UserOperationClaim<TId>(Id, operationClaim.Id));
    }

    public void RevokeOperationClaim(TId operationClaimId)
    {
        foreach (UserOperationClaim<TId> claim in OperationClaims.Where(c => c.OperationClaimId.Equals(operationClaimId)).ToList())
            OperationClaims.Remove(claim);
    }

    /// <summary>
    /// Kullanıcının tüm yetki adları: rollerinden gelenler + doğrudan verilenler. <c>UserRoles.Role.OperationClaims.OperationClaim</c>
    /// ve <c>OperationClaims.OperationClaim</c> yüklenmiş olmalı (yüklenmeyenler atlanır).
    /// </summary>
    public IReadOnlyList<string> GetPermissionNames() =>
        UserRoles
            .SelectMany(ur => ur.Role is null ? Enumerable.Empty<RoleOperationClaim<TId>>() : ur.Role.OperationClaims)
            .Select(rc => rc.OperationClaim?.Name)
            .Concat(OperationClaims.Select(uc => uc.OperationClaim?.Name))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

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
