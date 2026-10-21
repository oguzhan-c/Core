using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.Roles.Constants;
using CanAppTemplate.Application.Features.Roles.Rules;

namespace CanAppTemplate.Application.Features.Roles.Queries.GetById;

public sealed class GetByIdRoleQuery : IRequest<GetByIdRoleResponse>, ISecuredRequest
{
    public IdentityKey Id { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [RolesOperationClaims.Read];
}

public sealed class GetByIdRoleQueryHandler : IRequestHandler<GetByIdRoleQuery, GetByIdRoleResponse>
{
    private readonly RoleBusinessRules _roleBusinessRules;

    public GetByIdRoleQueryHandler(RoleBusinessRules roleBusinessRules) => _roleBusinessRules = roleBusinessRules;

    public async Task<GetByIdRoleResponse> Handle(GetByIdRoleQuery request, CancellationToken cancellationToken)
    {
        Role<IdentityKey> role = await _roleBusinessRules.RoleIdShouldExistWhenSelected(request.Id, cancellationToken);
        GetByIdRolePermissionDto[] permissions = role.OperationClaims
            .Where(rc => rc.OperationClaim is not null)
            .Select(rc => rc.OperationClaim!)
            .Select(c => new GetByIdRolePermissionDto(c.Id, c.Name, c.Description, c.IsDeprecated))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToArray();
        return new GetByIdRoleResponse(role.Id, role.Name, role.Description, role.IsSystem, permissions);
    }
}
