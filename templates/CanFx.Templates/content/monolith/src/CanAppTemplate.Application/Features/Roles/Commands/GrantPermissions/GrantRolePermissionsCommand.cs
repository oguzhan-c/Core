using CanFx.Application;
using CanFx.Application.Authorization;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Authorization;
using CanFx.Security.Entities;
using CanAppTemplate.Application.Features.OperationClaims.Rules;
using CanAppTemplate.Application.Features.Roles.Constants;
using CanAppTemplate.Application.Features.Roles.Rules;

namespace CanAppTemplate.Application.Features.Roles.Commands.GrantPermissions;

/// <summary>Role yetki verir. Aktör yalnızca kendi kapsadığı yetkilerle işlem yapabilir (<see cref="IAccessGrantGuard"/>).</summary>
public sealed class GrantRolePermissionsCommand : IRequest<GrantedRolePermissionsResponse>, ISecuredRequest, ILoggableRequest
{
    public IdentityKey RoleId { get; set; }

    public IReadOnlyCollection<IdentityKey> OperationClaimIds { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [RolesOperationClaims.Grant];
}

public sealed class GrantRolePermissionsCommandHandler : IRequestHandler<GrantRolePermissionsCommand, GrantedRolePermissionsResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleBusinessRules;
    private readonly OperationClaimBusinessRules _operationClaimBusinessRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public GrantRolePermissionsCommandHandler(
        IUnitOfWork unitOfWork,
        RoleBusinessRules roleBusinessRules,
        OperationClaimBusinessRules operationClaimBusinessRules,
        IAccessGrantGuard accessGrantGuard,
        IUserAuthorizationStore authorizationStore)
    {
        _unitOfWork = unitOfWork;
        _roleBusinessRules = roleBusinessRules;
        _operationClaimBusinessRules = operationClaimBusinessRules;
        _accessGrantGuard = accessGrantGuard;
        _authorizationStore = authorizationStore;
    }

    public async Task<GrantedRolePermissionsResponse> Handle(GrantRolePermissionsCommand request, CancellationToken cancellationToken)
    {
        _roleBusinessRules.RoleDefinitionsShouldBeManagedFromHost();
        Role<IdentityKey> role = await _roleBusinessRules.RoleIdShouldExistWhenSelected(request.RoleId, cancellationToken);
        _accessGrantGuard.EnsureCanManageRole(role.Name, role.IsSystem, RoleChange.Permissions);

        IReadOnlyList<OperationClaim<IdentityKey>> claims =
            await _operationClaimBusinessRules.OperationClaimsShouldExist(request.OperationClaimIds, allowDeprecated: false, cancellationToken);
        _accessGrantGuard.EnsureCanGrant(claims.Select(c => c.Name));

        foreach (OperationClaim<IdentityKey> claim in claims)
            role.GrantOperationClaim(claim);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.InvalidateAll(); // bu rolü taşıyan herkesin yetkisi değişti

        return new GrantedRolePermissionsResponse(role.Id, role.Name, RoleBusinessRules.PermissionNamesOf(role));
    }
}
