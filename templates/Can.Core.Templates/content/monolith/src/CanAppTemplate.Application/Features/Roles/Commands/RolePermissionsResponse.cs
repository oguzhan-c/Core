namespace CanAppTemplate.Application.Features.Roles.Commands;

public sealed record RolePermissionsResponse(Guid RoleId, string RoleName, IReadOnlyList<string> Permissions);
