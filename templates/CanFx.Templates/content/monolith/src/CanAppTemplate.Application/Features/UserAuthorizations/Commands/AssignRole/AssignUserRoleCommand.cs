using CanFx.Application;
using CanFx.Application.Authorization;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Authorization;
using CanFx.Security.Entities;
using CanAppTemplate.Application.Features.Roles.Rules;
using CanAppTemplate.Application.Features.UserAuthorizations.Constants;
using CanAppTemplate.Application.Features.UserAuthorizations.Rules;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.AssignRole;

/// <summary>
/// Kullanıcıya rol atar. Aktör rolün TÜM yetkilerini kapsamalı (Admin rolünü yalnızca Admin
/// verebilir) ve kendi rollerini değiştiremez. Değişiklik
/// token süresini beklemeden sonraki istekte geçerli olur.
/// </summary>
public sealed class AssignUserRoleCommand : IRequest<AssignedUserRoleResponse>, ISecuredRequest, ILoggableRequest
{
    public IdentityKey UserId { get; set; }

    public IdentityKey RoleId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [UserAuthorizationsOperationClaims.AssignRole];
}

public sealed class AssignUserRoleCommandHandler : IRequestHandler<AssignUserRoleCommand, AssignedUserRoleResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleBusinessRules;
    private readonly UserAuthorizationBusinessRules _userAuthorizationBusinessRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public AssignUserRoleCommandHandler(
        IUnitOfWork unitOfWork,
        RoleBusinessRules roleBusinessRules,
        UserAuthorizationBusinessRules userAuthorizationBusinessRules,
        IAccessGrantGuard accessGrantGuard,
        IUserAuthorizationStore authorizationStore)
    {
        _unitOfWork = unitOfWork;
        _roleBusinessRules = roleBusinessRules;
        _userAuthorizationBusinessRules = userAuthorizationBusinessRules;
        _accessGrantGuard = accessGrantGuard;
        _authorizationStore = authorizationStore;
    }

    public async Task<AssignedUserRoleResponse> Handle(AssignUserRoleCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _userAuthorizationBusinessRules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken);
        Role<IdentityKey> role = await _roleBusinessRules.RoleIdShouldExistWhenSelected(request.RoleId, cancellationToken);
        _accessGrantGuard.EnsureCanAssignRole(user.Id.ToString(), role.Name, RoleBusinessRules.PermissionNamesOf(role));

        user.AddRole(role);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.Invalidate(user.Id.ToString());

        return new AssignedUserRoleResponse(user.Id, role.Id, role.Name);
    }
}
