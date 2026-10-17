using Can.Core.Application;
using Can.Core.Application.Authorization;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.Roles.Rules;

namespace CanAppTemplate.Application.Features.Roles.Commands.Delete;

public sealed class DeleteRoleCommand : IRequest<DeletedRoleResponse>, ISecuredRequest, ILoggableRequest
{
    public Guid Id { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.RolesWrite];
}

public sealed class DeleteRoleCommandHandler : IRequestHandler<DeleteRoleCommand, DeletedRoleResponse>
{
    private readonly IRepository<Role<Guid>, Guid> _roleRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleBusinessRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public DeleteRoleCommandHandler(
        IRepository<Role<Guid>, Guid> roleRepository,
        IUnitOfWork unitOfWork,
        RoleBusinessRules roleBusinessRules,
        IAccessGrantGuard accessGrantGuard,
        IUserAuthorizationStore authorizationStore)
    {
        _roleRepository = roleRepository;
        _unitOfWork = unitOfWork;
        _roleBusinessRules = roleBusinessRules;
        _accessGrantGuard = accessGrantGuard;
        _authorizationStore = authorizationStore;
    }

    public async Task<DeletedRoleResponse> Handle(DeleteRoleCommand request, CancellationToken cancellationToken)
    {
        _roleBusinessRules.RoleDefinitionsShouldBeManagedFromHost();
        Role<Guid> role = await _roleBusinessRules.RoleIdShouldExistWhenSelected(request.Id, cancellationToken);
        _accessGrantGuard.EnsureCanManageRole(role.Name, role.IsSystem, RoleChange.Delete);
        await _roleBusinessRules.RoleShouldNotBeInUse(role.Id, cancellationToken);

        _roleRepository.Delete(role, permanent: true);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.InvalidateAll();

        return new DeletedRoleResponse(role.Id);
    }
}
