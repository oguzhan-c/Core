using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Sessions;
using CanFx.Security.Tokens;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.RefreshToken;

/// <summary>
/// Refresh token ile yeni token çifti (rotasyon; oturum aynı kalır). Kapatılmış bir token tekrar kullanılırsa token
/// çalınmış sayılır: kullanıcının tüm oturumları kapatılır ve kritik güvenlik olayı yayınlanır.
/// </summary>
public sealed record RefreshTokenCommand(string RefreshToken, ClientInfo Client) : IRequest<AuthResult>;

public sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, AuthResult>
{
    /// <summary>Aynı token'la eşzamanlı yenileme (sekmeler) için tolerans.</summary>
    public static readonly TimeSpan ConcurrentRefreshGrace = TimeSpan.FromSeconds(30);

    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<IdentityKey>, IdentityKey> _refreshTokens;
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly AuthBusinessRules _authBusinessRules;

    public RefreshTokenCommandHandler(
        ITokenService tokens,
        IRepository<RefreshToken<IdentityKey>, IdentityKey> refreshTokens,
        IRepository<AppUser, IdentityKey> users,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        AuthBusinessRules authBusinessRules)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _users = users;
        _tokenIssuer = tokenIssuer;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _authBusinessRules = authBusinessRules;
    }

    public async Task<AuthResult> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<IdentityKey>? token = await _refreshTokens.GetAsync(t => t.TokenHash == hash, cancellationToken: cancellationToken);
        _authBusinessRules.SessionShouldBeActive(token is not null);

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (token.IsRevoked)
        {
            // Rotasyonla kapanmış token'ın tekrar gelmesi: token kopyalanmış, hepsini kapat. Birkaç saniye içindeki tekrar
            // ise aynı tarayıcının iki sekmesinin aynı anda yenilemesidir (ilki yeni cookie'yi zaten yazdı): saldırı sayılmaz.
            if (token.RevokedReason == RefreshToken<IdentityKey>.RotatedReason && now - token.RevokedAt > ConcurrentRefreshGrace)
            {
                await _sessions.RevokeAllAsync(token.UserId.ToString(), cancellationToken);
                await _events.PublishAsync(
                    new SecurityEvent(SecurityEventTypes.RefreshTokenReused, token.UserId.ToString()) { SessionId = token.SessionId },
                    cancellationToken
                );
            }

            _authBusinessRules.SessionShouldBeActive(false);
        }

        AppUser? user = await _users.GetByIdAsync(token.UserId, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);
        _authBusinessRules.SessionShouldBeActive(user is not null && token.IsValidFor(now, user.SecurityStamp) && !user.IsLockedOut(now));

        AuthResult session = await _tokenIssuer.RotateAsync(user, token, request.Client, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return session;
    }
}
