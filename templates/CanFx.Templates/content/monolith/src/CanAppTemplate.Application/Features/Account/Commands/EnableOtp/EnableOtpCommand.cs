using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Otp;
using CanFx.Security.Passkeys;
using CanFx.Security.RecoveryCodes;
using Fido2NetLib;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Application.Features.Account.Rules;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.EnableOtp;

/// <summary>
/// Kurulumu tamamlar: uygulamadaki ilk kod doğruysa girişte artık bu uygulamanın kodu istenir. Kurtarma kodları
/// üretilip bir kez döndürülür.
/// </summary>
public sealed record EnableOtpCommand(string Code) : IRequest<RecoveryCodesDto>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class EnableOtpCommandHandler : IRequestHandler<EnableOtpCommand, RecoveryCodesDto>
{
    private readonly TwoFactorCodeVerifier _verifier;
    private readonly RecoveryCodeIssuer _recoveryCodes;
    private readonly ISecurityEvents _events;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public EnableOtpCommandHandler(
        TwoFactorCodeVerifier verifier,
        RecoveryCodeIssuer recoveryCodes,
        ISecurityEvents events,
        AuthBusinessRules authBusinessRules,
        AccountBusinessRules accountBusinessRules)
    {
        _verifier = verifier;
        _recoveryCodes = recoveryCodes;
        _events = events;
        _authBusinessRules = authBusinessRules;
        _accountBusinessRules = accountBusinessRules;
    }

    public async Task<RecoveryCodesDto> Handle(EnableOtpCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(cancellationToken: cancellationToken);
        _accountBusinessRules.OtpCodeShouldBeValid(await _verifier.VerifyOtpAsync(user.Id, request.Code, requireVerified: false, cancellationToken) is not null);

        user.SetAuthenticator(AuthenticatorType.Otp);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.TwoFactorEnabled, user.Id.ToString()) { Reason = "otp" }, cancellationToken);
        return await _recoveryCodes.RegenerateAsync(user.Id, cancellationToken);
    }
}
