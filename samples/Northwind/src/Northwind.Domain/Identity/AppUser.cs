using Can.Core.Domain.Results;
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
        FirstName = Check.Clean(firstName);
        LastName = Check.Clean(lastName);
    }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public Guid TenantId { get; set; }

    /// <summary>Yönetici tarafından oluşturulan (e-postası onaylı) kullanıcı.</summary>
    public static Result<AppUser> Create(string email, string firstName, string lastName, string passwordHash) =>
        Register(email, firstName, lastName, passwordHash).Tap(user => user.ConfirmEmail());

    /// <summary>Siteden kayıt olan kullanıcı: e-posta doğrulanana kadar giriş yapamaz.</summary>
    public static Result<AppUser> Register(string email, string firstName, string lastName, string passwordHash)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(firstName, "Ad", NameMaxLength),
            Check.Required(lastName, "Soyad", NameMaxLength)
        );
        if (valid.IsFailure)
            return valid.Errors;

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

/// <summary>
/// İnce taneli yetkiler (operation claim). Roller bu yetkileri <see cref="RoleGrants"/> ile alır; bir kullanıcıya
/// rolünden bağımsız da verilebilir. Admin rolü her şeyi geçer.
/// </summary>
public static class Permissions
{
    public const string ProductsStock = "products.stock";
    public const string OrdersCreate = "orders.create";
    public const string OrdersShip = "orders.ship";
    public const string OrdersCancel = "orders.cancel";

    public static readonly IReadOnlyList<(string Name, string Description)> All =
    [
        (ProductsStock, "Ürün stoğu ekleme"),
        (OrdersCreate, "Sipariş oluşturma"),
        (OrdersShip, "Siparişi kargoya verme"),
        (OrdersCancel, "Sipariş iptali"),
    ];

    /// <summary>Seed'de rollere verilen yetkiler.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RoleGrants = new Dictionary<string, string[]>
    {
        [Roles.Sales] = [OrdersCreate, OrdersCancel],
        [Roles.Warehouse] = [ProductsStock, OrdersShip],
    };
}
