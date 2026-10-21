using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Sessions;
using Can.Core.Security.Tokens;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.Logout;

/// <summary>Çıkış: bu oturumun refresh token'ı kapatılır (access token'ı ve cookie'leri WebApi geri alır).</summary>
public sealed record LogoutCommand(string? RefreshToken, ClientInfo Client) : IRequest;

public sealed class LogoutCommandHandler : IRequestHandler<LogoutCommand>
{
    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<IdentityKey>, IdentityKey> _refreshTokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public LogoutCommandHandler(ITokenService tokens, IRepository<RefreshToken<IdentityKey>, IdentityKey> refreshTokens, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return;

        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<IdentityKey>? token = await _refreshTokens.GetAsync(t => t.TokenHash == hash, cancellationToken: cancellationToken);
        if (token is null || token.IsRevoked)
            return;

        token.Revoke(_timeProvider.GetUtcNow(), request.Client.IpAddress, "logout");
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
