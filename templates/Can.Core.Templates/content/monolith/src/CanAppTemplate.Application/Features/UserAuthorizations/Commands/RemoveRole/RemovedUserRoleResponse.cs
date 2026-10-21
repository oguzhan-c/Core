namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.RemoveRole;

public sealed record RemovedUserRoleResponse(IdentityKey UserId, IdentityKey RoleId, string RoleName);
