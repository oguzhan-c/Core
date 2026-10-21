using CanFx.Domain.Auditing;
using CanFx.Domain.Results;
using CanFx.Security.Entities;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Domain.Entities;

/// <summary>Uygulamanın kullanıcısı (CanFx.Security <see cref="User{TId}"/> üzerine ad, soyad, telefon).</summary>
/// <remarks>TODO(can-gen): Uygulamaya özel kullanıcı alanlarını (ör. avatar, dil tercihi) buraya ekle.</remarks>
[Audited]
public sealed class AppUser : User<IdentityKey>
{
    public const int NameMaxLength = 50;
    public const int PhoneMaxLength = 20;

    private AppUser()
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    private AppUser(string email, string firstName, string lastName)
        : base(IdentityKeys.New(), email)
    {
        FirstName = Check.Clean(firstName);
        LastName = Check.Clean(lastName);
    }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public string? PhoneNumber { get; private set; }

    /// <summary>Kampanya e-postası / SMS izni.</summary>
    public bool MarketingConsent { get; private set; }
#if (MultiTenancy)

    /// <summary>
    /// Kullanıcının tenant'ı. Girişte token'a <c>tenant_id</c> olarak yazılır; tenant'a ait tablolar bu tenant'a göre
    /// süzülür. Boşsa kullanıcı host (platform) kullanıcısıdır.
    /// </summary>
    public Guid? TenantId { get; private set; }
#endif

    public string FullName => $"{FirstName} {LastName}";

    /// <summary>Kayıt olan kullanıcı: e-posta doğrulanana kadar giriş yapamaz.</summary>
    public static Result<AppUser> Register(string email, string firstName, string lastName, string passwordHash, bool marketingConsent = false)
    {
        Result<Success> valid = ValidateNames(firstName, lastName);
        if (valid.IsFailure)
            return valid.Errors;

        var user = new AppUser(email, firstName, lastName) { MarketingConsent = marketingConsent };
        user.SetPasswordHash(passwordHash);
        return user;
    }

    /// <summary>Yönetici ya da seed tarafından oluşturulan (e-postası onaylı) kullanıcı.</summary>
    public static Result<AppUser> Create(string email, string firstName, string lastName, string passwordHash) =>
        Register(email, firstName, lastName, passwordHash).Tap(user => user.ConfirmEmail());

    /// <summary>Dış sağlayıcıyla (Google, GitHub ...) ilk girişte oluşturulan kullanıcı: şifresi yoktur.</summary>
    public static Result<AppUser> RegisterExternal(string email, string firstName, string lastName)
    {
        Result<Success> valid = ValidateNames(firstName, lastName);
        if (valid.IsFailure)
            return valid.Errors;

        var user = new AppUser(email, firstName, lastName);
        user.ConfirmEmail();
        return user;
    }

    public Result<Success> UpdateProfile(string firstName, string lastName, string? phoneNumber, bool marketingConsent)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(firstName, "Ad", NameMaxLength),
            Check.Required(lastName, "Soyad", NameMaxLength),
            Check.Optional(phoneNumber, "Telefon", PhoneMaxLength)
        );
        if (valid.IsFailure)
            return valid;

        FirstName = Check.Clean(firstName);
        LastName = Check.Clean(lastName);
        PhoneNumber = Check.CleanOptional(phoneNumber);
        MarketingConsent = marketingConsent;
        return Result.Success;
    }
#if (MultiTenancy)

    public void AssignToTenant(Guid? tenantId) => TenantId = tenantId;
#endif

    public void AddRole(Role<IdentityKey> role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (UserRoles.All(r => r.RoleId != role.Id))
            UserRoles.Add(new UserRole<IdentityKey>(Id, role.Id));
    }

    public void RemoveRole(IdentityKey roleId)
    {
        UserRole<IdentityKey>? userRole = UserRoles.FirstOrDefault(r => r.RoleId == roleId);
        if (userRole is not null)
            UserRoles.Remove(userRole);
    }

    private static Result<Success> ValidateNames(string firstName, string lastName) =>
        Result.Validate(Check.Required(firstName, "Ad", NameMaxLength), Check.Required(lastName, "Soyad", NameMaxLength));
}
