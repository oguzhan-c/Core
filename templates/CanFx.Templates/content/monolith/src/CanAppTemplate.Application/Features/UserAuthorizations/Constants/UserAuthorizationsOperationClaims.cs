using CanFx.Security.Authorization;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Constants;

/// <summary>Kullanıcı rol / yetki yönetiminin yetkileri (Core: <see cref="IdentityPermissions"/>).</summary>
public static class UserAuthorizationsOperationClaims
{
    public const string Read = IdentityPermissions.UsersRead;
    public const string AssignRole = IdentityPermissions.UsersAssignRole;
    public const string Grant = IdentityPermissions.UsersGrant;
}
