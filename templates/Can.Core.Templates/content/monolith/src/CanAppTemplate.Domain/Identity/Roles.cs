namespace CanAppTemplate.Domain.Identity;

/// <summary>Roller. Admin her yetkiyi geçer.</summary>
/// <remarks>TODO(can-gen): Uygulamanın rollerini ekle (ör. Editor, Support); yetkilerini Application/Common/AppPermissions.RoleGrants içinde ver.</remarks>
public static class Roles
{
    public const string Admin = "Admin";

    /// <summary>Kayıt olan her kullanıcı.</summary>
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = [Admin, User];
}
