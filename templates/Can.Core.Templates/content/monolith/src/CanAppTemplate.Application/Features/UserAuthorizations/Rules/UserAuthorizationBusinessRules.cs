using Can.Core.Application.Exceptions;
using Can.Core.Application.Rules;
using Can.Core.Persistence.Repositories;
using CanAppTemplate.Application.Features.UserAuthorizations.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Rules;

public sealed class UserAuthorizationBusinessRules : BaseBusinessRules
{
    private readonly IRepository<AppUser, IdentityKey> _userRepository;

    public UserAuthorizationBusinessRules(IRepository<AppUser, IdentityKey> userRepository) => _userRepository = userRepository;

    /// <summary>Kullanıcıyı rolleri ve doğrudan yetkileriyle getirir (değişiklik izlenir); yoksa ya da başka tenant'taysa 404.</summary>
    public async Task<AppUser> UserIdShouldExistWhenSelected(IdentityKey userId, CancellationToken cancellationToken = default)
    {
        AppUser? user = await _userRepository.GetByIdAsync(userId, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);
        if (user is null)
            throw new NotFoundException(UserAuthorizationsBusinessMessages.UserNotExists) { Code = UserAuthorizationsBusinessMessages.UserNotExistsCode };
        return user;
    }

    /// <summary>Bu rolü taşıyan kullanıcı sayısı (tenant filtresi uygulanır: tenant içinde son yönetici korunur).</summary>
    public Task<int> CountUsersInRole(IdentityKey roleId, CancellationToken cancellationToken = default) =>
        _userRepository.CountAsync(u => u.UserRoles.Any(ur => ur.RoleId == roleId), cancellationToken: cancellationToken);

    /// <summary>Kullanıcının doğrudan verilmiş yetki adları (<see cref="UserIdShouldExistWhenSelected"/> ile yüklenmiş olmalı).</summary>
    public static IReadOnlyList<string> DirectPermissionNamesOf(AppUser user) =>
        user.OperationClaims
            .Where(uc => uc.OperationClaim is not null)
            .Select(uc => uc.OperationClaim!.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
