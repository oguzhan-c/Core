namespace CanAppTemplate.Application.Features.Roles.Commands.Create;

public sealed record CreatedRoleResponse(Guid Id, string Name, string? Description);
