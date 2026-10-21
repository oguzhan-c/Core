namespace CanAppTemplate.Application.Features.Roles.Commands;

public sealed record RolePermissionsResponse(IdentityKey RoleId, string RoleName, IReadOnlyList<string> Permissions);
