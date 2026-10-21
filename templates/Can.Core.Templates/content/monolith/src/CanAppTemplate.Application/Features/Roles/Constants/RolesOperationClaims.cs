using Can.Core.Security.Authorization;

namespace CanAppTemplate.Application.Features.Roles.Constants;

/// <summary>
/// Rol yönetiminin yetkileri. Değerler Core'daki <see cref="IdentityPermissions"/>'tandır ("yetki verme yetkisi"): Admin
/// bunları başka rollere verebilir. Katalog <c>AppOperationClaims.All</c>'a <c>IdentityPermissions.All</c> ile eklenir.
/// </summary>
public static class RolesOperationClaims
{
    public const string Read = IdentityPermissions.RolesRead;
    public const string Write = IdentityPermissions.RolesWrite;
    public const string Grant = IdentityPermissions.RolesGrant;
}
