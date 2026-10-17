namespace Can.Core.Security.Authorization;

/// <summary>
/// Rol / yetki yönetiminin kendi yetkileri ("yetki verme yetkisi"). Admin rolü hepsini kapsar; Admin bunları başka
/// rollere verebilir. Uygulamanın yetki kataloğuna eklenmeli (<see cref="All"/>).
/// </summary>
public static class IdentityPermissions
{
    /// <summary>Rol / yetki yönetiminin tamamı.</summary>
    public const string Manage = "identity.*";

    /// <summary>Rolleri, yetki kataloğunu ve rollerin yetkilerini görme.</summary>
    public const string RolesRead = "identity.roles.read";

    /// <summary>Rol oluşturma, adını / açıklamasını değiştirme, silme.</summary>
    public const string RolesWrite = "identity.roles.write";

    /// <summary>Rollere yetki verme / geri alma (yalnızca kendi kapsadığı yetkiler).</summary>
    public const string RolesGrant = "identity.roles.grant";

    /// <summary>Kullanıcıların rollerini ve doğrudan yetkilerini görme.</summary>
    public const string UsersRead = "identity.users.read";

    /// <summary>Kullanıcıya rol atama / rolden çıkarma (yalnızca tüm yetkilerini kapsadığı roller).</summary>
    public const string UsersAssignRole = "identity.users.assign-role";

    /// <summary>Kullanıcıya doğrudan yetki verme / geri alma (yalnızca kendi kapsadığı yetkiler).</summary>
    public const string UsersGrant = "identity.users.grant";

    public static IReadOnlyList<(string Name, string Description)> All { get; } =
    [
        (RolesRead, "Rolleri ve yetkileri görme"),
        (RolesWrite, "Rol oluşturma, düzenleme, silme"),
        (RolesGrant, "Rollere yetki verme / geri alma"),
        (UsersRead, "Kullanıcıların rollerini ve yetkilerini görme"),
        (UsersAssignRole, "Kullanıcıya rol atama / kaldırma"),
        (UsersGrant, "Kullanıcıya doğrudan yetki verme / geri alma"),
    ];
}
