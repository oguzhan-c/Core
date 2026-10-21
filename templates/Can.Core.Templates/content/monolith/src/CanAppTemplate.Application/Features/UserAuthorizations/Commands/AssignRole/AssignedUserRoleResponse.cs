namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.AssignRole;

public sealed record AssignedUserRoleResponse(IdentityKey UserId, IdentityKey RoleId, string RoleName);
