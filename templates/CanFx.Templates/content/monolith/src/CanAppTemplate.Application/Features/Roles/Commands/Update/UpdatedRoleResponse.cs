namespace CanAppTemplate.Application.Features.Roles.Commands.Update;

public sealed record UpdatedRoleResponse(IdentityKey Id, string Name, string? Description);
