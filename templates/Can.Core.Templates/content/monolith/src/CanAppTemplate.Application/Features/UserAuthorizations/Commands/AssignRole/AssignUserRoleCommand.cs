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

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.AssignRole;

/// <summary>
/// Kullanıcıya rol atar. Aktör rolün TÜM yetkilerini kapsamalı (Admin rolünü yalnızca Admin
/// verebilir) ve kendi rollerini değiştiremez.
/// </summary>
public sealed class AssignUserRoleCommand : IRequest<UserAuthorizationResponse>, ISecuredRequest, ILoggableRequest
{
    public IdentityKey UserId { get; set; }

    public IdentityKey RoleId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.UsersAssignRole];
}

public sealed class AssignUserRoleCommandValidator : AbstractValidator<AssignUserRoleCommand>
{
    public AssignUserRoleCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RoleId).NotEmpty();
    }
}

public sealed class AssignUserRoleCommandHandler : IRequestHandler<AssignUserRoleCommand, UserAuthorizationResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleRules;
    private readonly UserAuthorizationBusinessRules _userRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public AssignUserRoleCommandHandler(
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

    public async Task<UserAuthorizationResponse> Handle(AssignUserRoleCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _userRules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken);
        Role<IdentityKey> role = await _roleRules.RoleIdShouldExistWhenSelected(request.RoleId, cancellationToken);
        _accessGrantGuard.EnsureCanAssignRole(user.Id.ToString(), role.Name, RoleBusinessRules.PermissionNamesOf(role));

        user.AddRole(role);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.Invalidate(user.Id.ToString());

        return UserAuthorizationResponse.From(await _userRules.UserIdShouldExistWhenSelected(user.Id, cancellationToken));
    }
}
