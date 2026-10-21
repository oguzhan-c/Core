namespace CanAppTemplate.Application.Features.Roles.Commands.Create;

public sealed record CreatedRoleResponse(IdentityKey Id, string Name, string? Description);
