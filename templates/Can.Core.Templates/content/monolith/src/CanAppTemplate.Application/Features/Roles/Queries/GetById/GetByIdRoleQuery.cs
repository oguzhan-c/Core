using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.OperationClaims.Queries.GetList;
using CanAppTemplate.Application.Features.Roles.Rules;

namespace CanAppTemplate.Application.Features.Roles.Queries.GetById;

public sealed class GetByIdRoleQuery : IRequest<GetByIdRoleResponse>, ISecuredRequest
{
    public Guid Id { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.RolesRead];
}

public sealed record GetByIdRoleResponse(Guid Id, string Name, string? Description, bool IsSystem, IReadOnlyList<OperationClaimDto> Permissions);

public sealed class GetByIdRoleQueryHandler : IRequestHandler<GetByIdRoleQuery, GetByIdRoleResponse>
{
    private readonly RoleBusinessRules _roleBusinessRules;

    public GetByIdRoleQueryHandler(RoleBusinessRules roleBusinessRules) => _roleBusinessRules = roleBusinessRules;

    public async Task<GetByIdRoleResponse> Handle(GetByIdRoleQuery request, CancellationToken cancellationToken)
    {
        Role<Guid> role = await _roleBusinessRules.RoleIdShouldExistWhenSelected(request.Id, cancellationToken);
        OperationClaimDto[] permissions = role.OperationClaims
            .Where(rc => rc.OperationClaim is not null)
            .Select(rc => OperationClaimDto.From(rc.OperationClaim!))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToArray();
        return new GetByIdRoleResponse(role.Id, role.Name, role.Description, role.IsSystem, permissions);
    }
}
