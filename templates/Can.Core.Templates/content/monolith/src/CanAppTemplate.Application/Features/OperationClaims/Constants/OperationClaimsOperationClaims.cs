using Can.Core.Security.Authorization;

namespace CanAppTemplate.Application.Features.OperationClaims.Constants;

/// <summary>
/// Yetki kataloğunun yetkileri. Değerler Core'daki <see cref="IdentityPermissions"/>'tandır (katalog
/// <c>AppOperationClaims.All</c>'a <c>IdentityPermissions.All</c> ile eklenir).
/// </summary>
public static class OperationClaimsOperationClaims
{
    public const string Read = IdentityPermissions.RolesRead;
}
