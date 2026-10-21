using Can.Core.Application;
using Can.Core.Application.Authorization;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using FluentValidation;
using CanAppTemplate.Application.Features.Roles.Rules;
using CanAppTemplate.Application.Features.UserAuthorizations.Queries.GetByUserId;
using CanAppTemplate.Application.Features.UserAuthorizations.Rules;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.RemoveRole;

/// <summary>
/// Kullanıcıya rol kaldırır. Aktör rolün TÜM yetkilerini kapsamalı (Admin rolünü yalnızca Admin
/// alabilir) ve kendi rollerini değiştiremez. Son yönetici kaldırılamaz.
/// </summary>
public sealed class RemoveUserRoleCommand : IRequest<UserAuthorizationResponse>, ISecuredRequest, ILoggableRequest
{
    public IdentityKey UserId { get; set; }

    public IdentityKey RoleId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.UsersAssignRole];
}

public sealed class RemoveUserRoleCommandValidator : AbstractValidator<RemoveUserRoleCommand>
{
    public RemoveUserRoleCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RoleId).NotEmpty();
    }
}

public sealed class RemoveUserRoleCommandHandler : IRequestHandler<RemoveUserRoleCommand, UserAuthorizationResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleRules;
    private readonly UserAuthorizationBusinessRules _userRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public RemoveUserRoleCommandHandler(
        IUnitOfWork unitOfWork,
        RoleBusinessRules roleRules,
        UserAuthorizationBusinessRules userRules,
        IAccessGrantGuard accessGrantGuard,
        IUserAuthorizationStore authorizationStore)
    {
        _unitOfWork = unitOfWork;
        _roleRules = roleRules;
        _userRules = userRules;
        _accessGrantGuard = accessGrantGuard;
        _authorizationStore = authorizationStore;
    }

    public async Task<UserAuthorizationResponse> Handle(RemoveUserRoleCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _userRules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken);
        Role<IdentityKey> role = await _roleRules.RoleIdShouldExistWhenSelected(request.RoleId, cancellationToken);
        _accessGrantGuard.EnsureCanAssignRole(user.Id.ToString(), role.Name, RoleBusinessRules.PermissionNamesOf(role));

        if (_accessGrantGuard.IsAdminRole(role.Name) && user.UserRoles.Any(ur => ur.RoleId == role.Id))
            _accessGrantGuard.EnsureAdminRemains(await _userRules.CountUsersInRole(role.Id, cancellationToken) - 1);

        user.RemoveRole(role.Id);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.Invalidate(user.Id.ToString());

        return UserAuthorizationResponse.From(await _userRules.UserIdShouldExistWhenSelected(user.Id, cancellationToken));
    }
}
