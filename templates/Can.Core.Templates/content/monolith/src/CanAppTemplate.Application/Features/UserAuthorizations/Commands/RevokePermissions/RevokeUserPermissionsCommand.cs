using Can.Core.Application;
using Can.Core.Application.Authorization;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.OperationClaims.Rules;
using CanAppTemplate.Application.Features.UserAuthorizations.Constants;
using CanAppTemplate.Application.Features.UserAuthorizations.Rules;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.RevokePermissions;

/// <summary>
/// Kullanıcıya rolden bağımsız, doğrudan yetki geri alır. Aktör yalnızca kendi kapsadığı yetkilerle
/// işlem yapabilir ve kendi yetkilerini değiştiremez. İstisnalar içindir; kalıcı yetki setleri için rol tercih edilmeli.
/// </summary>
public sealed class RevokeUserPermissionsCommand : IRequest<RevokedUserPermissionsResponse>, ISecuredRequest, ILoggableRequest
{
    public IdentityKey UserId { get; set; }

    public IReadOnlyCollection<IdentityKey> OperationClaimIds { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [UserAuthorizationsOperationClaims.Grant];
}

public sealed class RevokeUserPermissionsCommandHandler : IRequestHandler<RevokeUserPermissionsCommand, RevokedUserPermissionsResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly OperationClaimBusinessRules _operationClaimBusinessRules;
    private readonly UserAuthorizationBusinessRules _userAuthorizationBusinessRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public RevokeUserPermissionsCommandHandler(
        IUnitOfWork unitOfWork,
        OperationClaimBusinessRules operationClaimBusinessRules,
        UserAuthorizationBusinessRules userAuthorizationBusinessRules,
        IAccessGrantGuard accessGrantGuard,
        IUserAuthorizationStore authorizationStore)
    {
        _unitOfWork = unitOfWork;
        _operationClaimBusinessRules = operationClaimBusinessRules;
        _userAuthorizationBusinessRules = userAuthorizationBusinessRules;
        _accessGrantGuard = accessGrantGuard;
        _authorizationStore = authorizationStore;
    }

    public async Task<RevokedUserPermissionsResponse> Handle(RevokeUserPermissionsCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _userAuthorizationBusinessRules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken);
        IReadOnlyList<OperationClaim<IdentityKey>> claims =
            await _operationClaimBusinessRules.OperationClaimsShouldExist(request.OperationClaimIds, allowDeprecated: true, cancellationToken);
        _accessGrantGuard.EnsureCanChangeUserPermissions(user.Id.ToString(), claims.Select(c => c.Name));

        foreach (OperationClaim<IdentityKey> claim in claims)
            user.RevokeOperationClaim(claim.Id);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.Invalidate(user.Id.ToString());

        return new RevokedUserPermissionsResponse(user.Id, UserAuthorizationBusinessRules.DirectPermissionNamesOf(user));
    }
}
