using Can.Core.Application;
using Can.Core.Application.Authorization;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.Roles.Rules;
using CanAppTemplate.Application.Features.UserAuthorizations.Constants;
using CanAppTemplate.Application.Features.UserAuthorizations.Rules;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.RemoveRole;

/// <summary>
/// Kullanıcıya rol kaldırır. Aktör rolün TÜM yetkilerini kapsamalı (Admin rolünü yalnızca Admin
/// alabilir) ve kendi rollerini değiştiremez. Son yönetici kaldırılamaz. Değişiklik
/// token süresini beklemeden sonraki istekte geçerli olur.
/// </summary>
public sealed class RemoveUserRoleCommand : IRequest<RemovedUserRoleResponse>, ISecuredRequest, ILoggableRequest
{
    public IdentityKey UserId { get; set; }

    public IdentityKey RoleId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [UserAuthorizationsOperationClaims.AssignRole];
}

public sealed class RemoveUserRoleCommandHandler : IRequestHandler<RemoveUserRoleCommand, RemovedUserRoleResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleBusinessRules;
    private readonly UserAuthorizationBusinessRules _userAuthorizationBusinessRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public RemoveUserRoleCommandHandler(
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

    public async Task<RemovedUserRoleResponse> Handle(RemoveUserRoleCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _userAuthorizationBusinessRules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken);
        Role<IdentityKey> role = await _roleBusinessRules.RoleIdShouldExistWhenSelected(request.RoleId, cancellationToken);
        _accessGrantGuard.EnsureCanAssignRole(user.Id.ToString(), role.Name, RoleBusinessRules.PermissionNamesOf(role));

        if (_accessGrantGuard.IsAdminRole(role.Name) && user.UserRoles.Any(ur => ur.RoleId == role.Id))
            _accessGrantGuard.EnsureAdminRemains(await _userAuthorizationBusinessRules.CountUsersInRole(role.Id, cancellationToken) - 1);

        user.RemoveRole(role.Id);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.Invalidate(user.Id.ToString());

        return new RemovedUserRoleResponse(user.Id, role.Id, role.Name);
    }
}
