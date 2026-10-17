using Can.Core.Application;
using Can.Core.Application.Authorization;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using FluentValidation;
using CanAppTemplate.Application.Features.Roles.Rules;

namespace CanAppTemplate.Application.Features.Roles.Commands.GrantPermissions;

/// <summary>Role yetki verir. Aktör yalnızca kendi kapsadığı yetkilerle işlem yapabilir (<see cref="IAccessGrantGuard"/>).</summary>
public sealed class GrantRolePermissionsCommand : IRequest<RolePermissionsResponse>, ISecuredRequest, ILoggableRequest
{
    public Guid RoleId { get; set; }

    public IReadOnlyCollection<Guid> OperationClaimIds { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.RolesGrant];
}

public sealed class GrantRolePermissionsCommandValidator : AbstractValidator<GrantRolePermissionsCommand>
{
    public GrantRolePermissionsCommandValidator()
    {
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.OperationClaimIds).NotEmpty();
    }
}

public sealed class GrantRolePermissionsCommandHandler : IRequestHandler<GrantRolePermissionsCommand, RolePermissionsResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleBusinessRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public GrantRolePermissionsCommandHandler(
        IUnitOfWork unitOfWork,
        RoleBusinessRules roleBusinessRules,
        IAccessGrantGuard accessGrantGuard,
        IUserAuthorizationStore authorizationStore)
    {
        _unitOfWork = unitOfWork;
        _roleBusinessRules = roleBusinessRules;
        _accessGrantGuard = accessGrantGuard;
        _authorizationStore = authorizationStore;
    }

    public async Task<RolePermissionsResponse> Handle(GrantRolePermissionsCommand request, CancellationToken cancellationToken)
    {
        _roleBusinessRules.RoleDefinitionsShouldBeManagedFromHost();
        Role<Guid> role = await _roleBusinessRules.RoleIdShouldExistWhenSelected(request.RoleId, cancellationToken);
        _accessGrantGuard.EnsureCanManageRole(role.Name, role.IsSystem, RoleChange.Permissions);

        IReadOnlyList<OperationClaim<Guid>> claims =
            await _roleBusinessRules.OperationClaimsShouldExist(request.OperationClaimIds, allowDeprecated: false, cancellationToken);
        _accessGrantGuard.EnsureCanGrant(claims.Select(c => c.Name));

        foreach (OperationClaim<Guid> claim in claims)
            role.GrantOperationClaim(claim);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.InvalidateAll(); // bu rolü taşıyan herkesin yetkisi değişti

        Role<Guid> updated = await _roleBusinessRules.RoleIdShouldExistWhenSelected(role.Id, cancellationToken);
        return new RolePermissionsResponse(updated.Id, updated.Name, RoleBusinessRules.PermissionNamesOf(updated));
    }
}
