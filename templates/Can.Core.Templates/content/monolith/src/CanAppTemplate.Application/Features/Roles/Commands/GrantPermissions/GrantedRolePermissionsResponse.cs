namespace CanAppTemplate.Application.Features.Roles.Commands.GrantPermissions;

/// <summary>İşlemden sonra rolün etkin yetkileri.</summary>
public sealed record GrantedRolePermissionsResponse(IdentityKey RoleId, string RoleName, IReadOnlyList<string> Permissions);
