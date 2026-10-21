using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Otp;
using Can.Core.Security.RecoveryCodes;
using Can.Core.Security.Sessions;
using FluentValidation;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.CompleteTwoFactorLogin;

/// <summary>
/// İki adımlı girişin ikinci adımı: authenticator uygulamasındaki ya da e-postaya gelen 6 haneli kod, ya da cihaz
/// kaybolduysa kurtarma kodlarından biri. <see cref="Challenge"/> sunucunun şifreleyip cookie'de sakladığı bilgidir;
/// istemci değiştiremez.
/// </summary>
public sealed record CompleteTwoFactorLoginCommand(TwoFactorChallenge Challenge, string Code, bool IsRecoveryCode, ClientInfo Client)
    : IRequest<Result<AuthResult>>, ILoggableRequest;

public sealed class CompleteTwoFactorLoginCommandHandler : IRequestHandler<CompleteTwoFactorLoginCommand, Result<AuthResult>>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly IRepository<RecoveryCode<IdentityKey>, IdentityKey> _recoveryCodes;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly TwoFactorCodeVerifier _verifier;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public CompleteTwoFactorLoginCommandHandler(
        IRepository<AppUser, IdentityKey> users,
        IRepository<RecoveryCode<IdentityKey>, IdentityKey> recoveryCodes,
        IRecoveryCodeService recoveryCodeService,
        TwoFactorCodeVerifier verifier,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _users = users;
        _recoveryCodes = recoveryCodes;
        _recoveryCodeService = recoveryCodeService;
        _verifier = verifier;
        _tokenIssuer = tokenIssuer;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AuthResult>> Handle(CompleteTwoFactorLoginCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> resolved = await TwoFactorChallenges.ResolveUserAsync(_users, request.Challenge, cancellationToken);
        if (resolved.IsFailure)
            return resolved.Errors;

        AppUser user = resolved.Value;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (user.IsLockedOut(now))
            return AuthErrors.LockedOut;

        bool valid = request.IsRecoveryCode
            ? await UseRecoveryCodeAsync(user, request.Code, now, cancellationToken)
            : await _verifier.VerifyAsync(user, request.Code, cancellationToken);

        if (!valid)
        {
            // Kaba kuvvete karşı: hatalı kodlar şifre hataları gibi sayılır ve hesabı kilitler.
            user.RegisterFailedAccess(now);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await AuthEvents.FailedAsync(_events, user, now, request.IsRecoveryCode ? "invalid_recovery_code" : "invalid_code", cancellationToken, SecurityEventTypes.TwoFactorFailed);
            return user.IsLockedOut(now) ? AuthErrors.LockedOut : AuthErrors.InvalidTwoFactorCode;
        }

        user.ResetAccessFailed();
        await _events.PublishAsync(
            new SecurityEvent(request.IsRecoveryCode ? SecurityEventTypes.RecoveryCodeUsed : SecurityEventTypes.TwoFactorSucceeded, user.Id.ToString()),
            cancellationToken
        );

        AuthResult result = await AuthEvents.SignInAsync(user, request.Client, "two_factor", _tokenIssuer, _sessions, _events, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<bool> UseRecoveryCodeAsync(AppUser user, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string hash = _recoveryCodeService.Hash(code);
        RecoveryCode<IdentityKey>? recovery = await _recoveryCodes.GetAsync(r => r.UserId == user.Id && r.CodeHash == hash && r.UsedAt == null, cancellationToken: cancellationToken);
        if (recovery is null)
            return false;

        recovery.MarkUsed(now);
        return true;
    }
}
