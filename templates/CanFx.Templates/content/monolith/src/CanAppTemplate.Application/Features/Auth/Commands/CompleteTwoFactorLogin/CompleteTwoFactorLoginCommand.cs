using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Otp;
using CanFx.Security.RecoveryCodes;
using CanFx.Security.Sessions;
using FluentValidation;
using CanAppTemplate.Application.Features.Auth.Rules;
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
    : IRequest<AuthResult>, ILoggableRequest;

public sealed class CompleteTwoFactorLoginCommandHandler : IRequestHandler<CompleteTwoFactorLoginCommand, AuthResult>
{
    private readonly IRepository<RecoveryCode<IdentityKey>, IdentityKey> _recoveryCodes;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly TwoFactorCodeVerifier _verifier;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly AuthBusinessRules _authBusinessRules;

    public CompleteTwoFactorLoginCommandHandler(
        IRepository<RecoveryCode<IdentityKey>, IdentityKey> recoveryCodes,
        IRecoveryCodeService recoveryCodeService,
        TwoFactorCodeVerifier verifier,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        AuthBusinessRules authBusinessRules)
    {
        _recoveryCodes = recoveryCodes;
        _recoveryCodeService = recoveryCodeService;
        _verifier = verifier;
        _tokenIssuer = tokenIssuer;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _authBusinessRules = authBusinessRules;
    }

    public async Task<AuthResult> Handle(CompleteTwoFactorLoginCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.TwoFactorChallengeShouldBeValid(request.Challenge, cancellationToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        _authBusinessRules.UserShouldNotBeLockedOut(user, now);

        bool valid = request.IsRecoveryCode
            ? await UseRecoveryCodeAsync(user, request.Code, now, cancellationToken)
            : await _verifier.VerifyAsync(user, request.Code, cancellationToken);

        if (!valid)
        {
            // Kaba kuvvete karşı: hatalı kodlar şifre hataları gibi sayılır ve hesabı kilitler (exception'dan ÖNCE kaydedilir).
            user.RegisterFailedAccess(now);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await AuthEvents.FailedAsync(_events, user, now, request.IsRecoveryCode ? "invalid_recovery_code" : "invalid_code", cancellationToken, SecurityEventTypes.TwoFactorFailed);
        }

        _authBusinessRules.TwoFactorCodeShouldBeValid(valid, user, now);

        user.ResetAccessFailed();
        await _events.PublishAsync(
            new SecurityEvent(request.IsRecoveryCode ? SecurityEventTypes.RecoveryCodeUsed : SecurityEventTypes.TwoFactorSucceeded, user.Id.ToString()),
            cancellationToken
        );

        AuthResult session = await AuthEvents.SignInAsync(user, request.Client, "two_factor", _tokenIssuer, _sessions, _events, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return session;
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
