namespace CanAppTemplate.Application.Features.Roles.Queries.GetById;

public sealed record GetByIdRoleResponse(IdentityKey Id, string Name, string? Description, bool IsSystem, IReadOnlyList<GetByIdRolePermissionDto> Permissions);

public sealed record GetByIdRolePermissionDto(IdentityKey Id, string Name, string? Description, bool IsDeprecated);
