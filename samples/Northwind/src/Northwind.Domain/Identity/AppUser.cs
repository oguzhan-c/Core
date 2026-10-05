using Can.Core.Domain.Auditing;
using Can.Core.Domain.MultiTenancy;
using Can.Core.Security.Entities;
using Northwind.Domain.Common;

namespace Northwind.Domain.Identity;

/// <summary>Uygulama kullanıcısı. Her kullanıcı bir mağazaya (tenant) aittir; giriş o mağaza adına yapılır.</summary>
[Audited]
public sealed class AppUser : User<Guid>, IMultiTenant<Guid>
{
    public const int NameMaxLength = 50;

    private AppUser()
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    private AppUser(string email, string firstName, string lastName)
        : base(Guid.CreateVersion7(), email)
    {
        FirstName = Check.Required(firstName, "Ad", NameMaxLength);
        LastName = Check.Required(lastName, "Soyad", NameMaxLength);
    }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public Guid TenantId { get; set; }

    /// <summary>Yönetici tarafından oluşturulan (e-postası onaylı) kullanıcı.</summary>
    public static AppUser Create(string email, string firstName, string lastName, string passwordHash)
    {
        AppUser user = Register(email, firstName, lastName, passwordHash);
        user.ConfirmEmail();
        return user;
    }

    /// <summary>Siteden kayıt olan kullanıcı: e-posta doğrulanana kadar giriş yapamaz.</summary>
    public static AppUser Register(string email, string firstName, string lastName, string passwordHash)
    {
        var user = new AppUser(email, firstName, lastName);
        user.SetPasswordHash(passwordHash);
        return user;
    }

    public void AddRole(Role<Guid> role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (UserRoles.All(r => r.RoleId != role.Id))
            UserRoles.Add(new UserRole<Guid>(Id, role.Id));
    }
}

/// <summary>Uygulamadaki roller.</summary>
public static class Roles
{
    /// <summary>Her şeyi yapabilir (ürün, fiyat, kullanıcı, denetim kayıtları).</summary>
    public const string Admin = "Admin";

    /// <summary>Müşteri ve sipariş işlemleri.</summary>
    public const string Sales = "Sales";

    /// <summary>Stok ve kargo işlemleri.</summary>
    public const string Warehouse = "Warehouse";

    /// <summary>Siteden kayıt olan müşteri: katalog, sepet ve kendi siparişleri.</summary>
    public const string Customer = "Customer";

    public static readonly IReadOnlyList<string> All = [Admin, Sales, Warehouse, Customer];

    /// <summary>Yönetim panelini kullanabilen roller (Admin her zaman geçer).</summary>
    public static readonly IReadOnlyCollection<string> Staff = [Sales, Warehouse];
}
