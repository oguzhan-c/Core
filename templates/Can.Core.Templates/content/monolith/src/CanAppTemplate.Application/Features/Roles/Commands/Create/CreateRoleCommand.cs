using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.Roles.Rules;

namespace CanAppTemplate.Application.Features.Roles.Commands.Create;

public sealed class CreateRoleCommand : IRequest<CreatedRoleResponse>, ISecuredRequest, ILoggableRequest
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.RolesWrite];
}

public sealed class CreateRoleCommandHandler : IRequestHandler<CreateRoleCommand, CreatedRoleResponse>
{
    private readonly IRepository<Role<Guid>, Guid> _roleRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleBusinessRules;

    public CreateRoleCommandHandler(IRepository<Role<Guid>, Guid> roleRepository, IUnitOfWork unitOfWork, RoleBusinessRules roleBusinessRules)
    {
        _roleRepository = roleRepository;
        _unitOfWork = unitOfWork;
        _roleBusinessRules = roleBusinessRules;
    }

    public async Task<CreatedRoleResponse> Handle(CreateRoleCommand request, CancellationToken cancellationToken)
    {
        _roleBusinessRules.RoleDefinitionsShouldBeManagedFromHost();
        await _roleBusinessRules.RoleNameCanNotBeDuplicated(request.Name, exceptId: null, cancellationToken);

        var role = new Role<Guid>(Guid.CreateVersion7(), request.Name);
        role.SetDescription(request.Description);

        await _roleRepository.AddAsync(role, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new CreatedRoleResponse(role.Id, role.Name, role.Description);
    }
}
