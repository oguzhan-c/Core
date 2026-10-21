using CanFx.Application;
using CanFx.Application.Authorization;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Authorization;
using CanFx.Security.Entities;
using CanAppTemplate.Application.Features.OperationClaims.Rules;
using CanAppTemplate.Application.Features.UserAuthorizations.Constants;
using CanAppTemplate.Application.Features.UserAuthorizations.Rules;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.GrantPermissions;

/// <summary>
/// Kullanıcıya rolden bağımsız, doğrudan yetki verir. Aktör yalnızca kendi kapsadığı yetkilerle
/// işlem yapabilir ve kendi yetkilerini değiştiremez. İstisnalar içindir; kalıcı yetki setleri için rol tercih edilmeli.
/// </summary>
public sealed class GrantUserPermissionsCommand : IRequest<GrantedUserPermissionsResponse>, ISecuredRequest, ILoggableRequest
{
    public IdentityKey UserId { get; set; }

    public IReadOnlyCollection<IdentityKey> OperationClaimIds { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [UserAuthorizationsOperationClaims.Grant];
}

public sealed class GrantUserPermissionsCommandHandler : IRequestHandler<GrantUserPermissionsCommand, GrantedUserPermissionsResponse>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly OperationClaimBusinessRules _operationClaimBusinessRules;
    private readonly UserAuthorizationBusinessRules _userAuthorizationBusinessRules;
    private readonly IAccessGrantGuard _accessGrantGuard;
    private readonly IUserAuthorizationStore _authorizationStore;

    public GrantUserPermissionsCommandHandler(
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

    public async Task<GrantedUserPermissionsResponse> Handle(GrantUserPermissionsCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _userAuthorizationBusinessRules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken);
        IReadOnlyList<OperationClaim<IdentityKey>> claims =
            await _operationClaimBusinessRules.OperationClaimsShouldExist(request.OperationClaimIds, allowDeprecated: false, cancellationToken);
        _accessGrantGuard.EnsureCanChangeUserPermissions(user.Id.ToString(), claims.Select(c => c.Name));

        foreach (OperationClaim<IdentityKey> claim in claims)
            user.GrantOperationClaim(claim);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        _authorizationStore.Invalidate(user.Id.ToString());

        return new GrantedUserPermissionsResponse(user.Id, UserAuthorizationBusinessRules.DirectPermissionNamesOf(user));
    }
}
