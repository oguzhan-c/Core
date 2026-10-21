using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Otp;
using Can.Core.Security.Passkeys;
using Can.Core.Security.RecoveryCodes;
using Fido2NetLib;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Application.Features.Account.Rules;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.EnableEmailTwoFactor;

public sealed record EnableEmailTwoFactorCommand : IRequest<RecoveryCodesDto>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class EnableEmailTwoFactorCommandHandler : IRequestHandler<EnableEmailTwoFactorCommand, RecoveryCodesDto>
{
    private readonly IRepository<OtpAuthenticator<IdentityKey>, IdentityKey> _otpAuthenticators;
    private readonly RecoveryCodeIssuer _recoveryCodes;
    private readonly ISecurityEvents _events;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public EnableEmailTwoFactorCommandHandler(
        IRepository<OtpAuthenticator<IdentityKey>, IdentityKey> otpAuthenticators,
        RecoveryCodeIssuer recoveryCodes,
        ISecurityEvents events,
        AuthBusinessRules authBusinessRules,
        AccountBusinessRules accountBusinessRules)
    {
        _otpAuthenticators = otpAuthenticators;
        _recoveryCodes = recoveryCodes;
        _events = events;
        _authBusinessRules = authBusinessRules;
        _accountBusinessRules = accountBusinessRules;
    }

    public async Task<RecoveryCodesDto> Handle(EnableEmailTwoFactorCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(cancellationToken: cancellationToken);
        _accountBusinessRules.EmailShouldBeConfirmedForTwoFactor(user);

        if (await _otpAuthenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken) is { } otp)
            _otpAuthenticators.Delete(otp, permanent: true);

        user.SetAuthenticator(AuthenticatorType.Email);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.TwoFactorEnabled, user.Id.ToString()) { Reason = "email" }, cancellationToken);
        return await _recoveryCodes.RegenerateAsync(user.Id, cancellationToken);
    }
}
