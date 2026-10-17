using Can.Core.Application;
using Can.Core.Application.Authorization;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.Roles.Rules;

namespace CanAppTemplate.Application.Features.Roles.Commands.Update;

public sealed class UpdateRoleCommand : IRequest<UpdatedRoleResponse>, ISecuredRequest, ILoggableRequest
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.RolesWrite];
}

public sealed class UpdateRoleCommandHandler : IRequestHandler<UpdateRoleCommand, UpdatedRoleResponse>
{
    private readonly IRepository<Role<Guid>, Guid> _roleRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleBusinessRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public UpdateRoleCommandHandler(
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

    public async Task<UpdatedRoleResponse> Handle(UpdateRoleCommand request, CancellationToken cancellationToken)
    {
        _roleBusinessRules.RoleDefinitionsShouldBeManagedFromHost();
        Role<Guid> role = await _roleBusinessRules.RoleIdShouldExistWhenSelected(request.Id, cancellationToken);

        bool renamed = !string.Equals(role.Name, request.Name.Trim(), StringComparison.Ordinal);
        _accessGrantGuard.EnsureCanManageRole(role.Name, role.IsSystem, renamed ? RoleChange.Rename : RoleChange.Details);
        if (renamed)
        {
            await _roleBusinessRules.RoleNameCanNotBeDuplicated(request.Name, exceptId: role.Id, cancellationToken);
            role.Rename(request.Name);
        }

        role.SetDescription(request.Description);
        _roleRepository.Update(role);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (renamed)
            _authorizationStore.InvalidateAll(); // rol adları önbellekte

        return new UpdatedRoleResponse(role.Id, role.Name, role.Description);
    }
}
