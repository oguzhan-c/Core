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

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.RevokePermissions;

/// <summary>
/// Kullanıcıya rolden bağımsız, doğrudan yetki geri alır. Aktör yalnızca kendi kapsadığı yetkilerle
/// işlem yapabilir ve kendi yetkilerini değiştiremez. İstisnalar içindir; kalıcı yetki setleri için rol tercih edilmeli.
/// </summary>
public sealed class RevokeUserPermissionsCommand : IRequest<UserAuthorizationResponse>, ISecuredRequest, ILoggableRequest
{
    public Guid UserId { get; set; }

    public IReadOnlyCollection<Guid> OperationClaimIds { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.UsersGrant];
}

public sealed class RevokeUserPermissionsCommandValidator : AbstractValidator<RevokeUserPermissionsCommand>
{
    public RevokeUserPermissionsCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.OperationClaimIds).NotEmpty();
    }
}

public sealed class RevokeUserPermissionsCommandHandler : IRequestHandler<RevokeUserPermissionsCommand, UserAuthorizationResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoleBusinessRules _roleRules;
    private readonly UserAuthorizationBusinessRules _userRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public RevokeUserPermissionsCommandHandler(
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

    public async Task<UserAuthorizationResponse> Handle(RevokeUserPermissionsCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _userRules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken);
        IReadOnlyList<OperationClaim<Guid>> claims =
            await _roleRules.OperationClaimsShouldExist(request.OperationClaimIds, allowDeprecated: true, cancellationToken);
        _accessGrantGuard.EnsureCanChangeUserPermissions(user.Id.ToString(), claims.Select(c => c.Name));

        foreach (OperationClaim<Guid> claim in claims)
            user.RevokeOperationClaim(claim.Id);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.Invalidate(user.Id.ToString());

        return UserAuthorizationResponse.From(await _userRules.UserIdShouldExistWhenSelected(user.Id, cancellationToken));
    }
}
