namespace CanAppTemplate.Application.Features.Roles.Queries.GetList;

public sealed record GetListRoleListItemDto(IdentityKey Id, string Name, string? Description, bool IsSystem, int PermissionCount);
