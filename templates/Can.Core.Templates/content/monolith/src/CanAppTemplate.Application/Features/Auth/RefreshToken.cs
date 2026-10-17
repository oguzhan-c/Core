using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Sessions;
using Can.Core.Security.Tokens;
using CanAppTemplate.Application.Common;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth;

/// <summary>
/// Refresh token ile yeni token çifti (rotasyon; oturum aynı kalır). Kapatılmış bir token tekrar kullanılırsa token
/// çalınmış sayılır: kullanıcının tüm oturumları kapatılır ve kritik güvenlik olayı yayınlanır.
/// </summary>
public sealed record RefreshTokenCommand(string RefreshToken, ClientInfo Client) : IRequest<Result<AuthResult>>;

public sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, Result<AuthResult>>
{
    /// <summary>Aynı token'la eşzamanlı yenileme (sekmeler) için tolerans.</summary>
    public static readonly TimeSpan ConcurrentRefreshGrace = TimeSpan.FromSeconds(30);

    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<Guid>, Guid> _refreshTokens;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public RefreshTokenCommandHandler(
        ITokenService tokens,
        IRepository<RefreshToken<Guid>, Guid> refreshTokens,
        IRepository<AppUser, Guid> users,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _users = users;
        _tokenIssuer = tokenIssuer;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AuthResult>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<Guid>? token = await _refreshTokens.GetAsync(t => t.TokenHash == hash, cancellationToken: cancellationToken);
        if (token is null)
            return AuthErrors.SessionExpired;

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (token.IsRevoked)
        {
            // Rotasyonla kapanmış token'ın tekrar gelmesi: token kopyalanmış, hepsini kapat. Birkaç saniye içindeki tekrar
            // ise aynı tarayıcının iki sekmesinin aynı anda yenilemesidir (ilki yeni cookie'yi zaten yazdı): saldırı sayılmaz.
            if (token.RevokedReason == RefreshToken<Guid>.RotatedReason && now - token.RevokedAt > ConcurrentRefreshGrace)
            {
                await _sessions.RevokeAllAsync(token.UserId.ToString(), cancellationToken);
                await _events.PublishAsync(
                    new SecurityEvent(SecurityEventTypes.RefreshTokenReused, token.UserId.ToString()) { SessionId = token.SessionId },
                    cancellationToken
                );
            }

            return AuthErrors.SessionExpired;
        }

        AppUser? user = await _users.GetByIdAsync(token.UserId, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);
        if (user is null || !token.IsValidFor(now, user.SecurityStamp) || user.IsLockedOut(now))
            return AuthErrors.SessionExpired;

        AuthResult result = await _tokenIssuer.RotateAsync(user, token, request.Client, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>Çıkış: bu oturumun refresh token'ı kapatılır (access token'ı ve cookie'leri WebApi geri alır).</summary>
public sealed record LogoutCommand(string? RefreshToken, ClientInfo Client) : IRequest<Result<Success>>;

public sealed class LogoutCommandHandler : IRequestHandler<LogoutCommand, Result<Success>>
{
    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<Guid>, Guid> _refreshTokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public LogoutCommandHandler(ITokenService tokens, IRepository<RefreshToken<Guid>, Guid> refreshTokens, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<Success>> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return Result.Success;

        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<Guid>? token = await _refreshTokens.GetAsync(t => t.TokenHash == hash, cancellationToken: cancellationToken);
        if (token is null || token.IsRevoked)
            return Result.Success;

        token.Revoke(_timeProvider.GetUtcNow(), request.Client.IpAddress, "logout");
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success;
    }
}
