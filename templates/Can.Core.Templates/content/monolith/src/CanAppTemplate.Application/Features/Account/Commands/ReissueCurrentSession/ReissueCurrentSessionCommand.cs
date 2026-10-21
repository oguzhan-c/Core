using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using CanAppTemplate.Application.Features.Auth.Commands.RefreshToken;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.ReissueCurrentSession;

/// <summary>
/// Güvenlik damgasını değiştiren bir işlemden (şifre, 2FA, dış hesap) hemen sonra, AYNI istekte, işlemi yapan oturuma
/// yeni damgalı token'lar verir: diğer cihazlar kapanır, bu cihaz açık kalır. Mevcut refresh token aktif olmalı ve bu
/// kullanıcıya ait olmalı; damgası eski olduğu için normal yenileme (<see cref="RefreshTokenCommand"/>) burada kullanılamaz.
/// </summary>
public sealed record ReissueCurrentSessionCommand(string? RefreshToken, ClientInfo Client) : IRequest<Result<AuthResult>>, ISecuredRequest;

public sealed class ReissueCurrentSessionCommandHandler : IRequestHandler<ReissueCurrentSessionCommand, Result<AuthResult>>
{
    private readonly ICurrentUser _currentUser;
    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<IdentityKey>, IdentityKey> _refreshTokens;
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly ISecurityStampValidator? _stampValidator;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ReissueCurrentSessionCommandHandler(
        ICurrentUser currentUser,
        ITokenService tokens,
        IRepository<RefreshToken<IdentityKey>, IdentityKey> refreshTokens,
        IRepository<AppUser, IdentityKey> users,
        AuthTokenIssuer tokenIssuer,
        IEnumerable<ISecurityStampValidator> stampValidators,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _currentUser = currentUser;
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _users = users;
        _tokenIssuer = tokenIssuer;
        _stampValidator = stampValidators.FirstOrDefault();
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AuthResult>> Handle(ReissueCurrentSessionCommand request, CancellationToken cancellationToken)
    {
        Result<IdentityKey> userId = _currentUser.UserId();
        if (userId.IsFailure)
            return userId.Errors;

        // Diğer sunuculardaki önbellek de en geç önbellek süresi sonunda yeni damgayı görür.
        _stampValidator?.Invalidate(userId.Value.ToString());

        if (string.IsNullOrEmpty(request.RefreshToken))
            return AuthErrors.SessionExpired;

        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<IdentityKey>? token = await _refreshTokens.GetAsync(t => t.TokenHash == hash && t.UserId == userId.Value, cancellationToken: cancellationToken);
        if (token is null || !token.IsActive(_timeProvider.GetUtcNow()))
            return AuthErrors.SessionExpired;

        AppUser? user = await _users.GetByIdAsync(userId.Value, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        AuthResult result = await _tokenIssuer.RotateAsync(user, token, request.Client, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
