namespace CanAppTemplate.Application.Features.Roles.Commands.RevokePermissions;

/// <summary>İşlemden sonra rolün etkin yetkileri.</summary>
public sealed record RevokedRolePermissionsResponse(IdentityKey RoleId, string RoleName, IReadOnlyList<string> Permissions);
