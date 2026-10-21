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
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ResendTwoFactorCode;

/// <summary>E-posta ile ikinci adımda yeni kod gönderir.</summary>
public sealed record ResendTwoFactorCodeCommand(TwoFactorChallenge Challenge) : IRequest, ITransactionalRequest;

public sealed class ResendTwoFactorCodeCommandHandler : IRequestHandler<ResendTwoFactorCodeCommand>
{
    private readonly EmailVerification _emailVerification;
    private readonly AuthBusinessRules _authBusinessRules;

    public ResendTwoFactorCodeCommandHandler(EmailVerification emailVerification, AuthBusinessRules authBusinessRules)
    {
        _emailVerification = emailVerification;
        _authBusinessRules = authBusinessRules;
    }

    public async Task Handle(ResendTwoFactorCodeCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.TwoFactorChallengeShouldBeValid(request.Challenge, cancellationToken);
        _authBusinessRules.TwoFactorShouldUseEmail(user);
        await _emailVerification.SendCodeAsync(user, VerificationPurpose.SignIn, cancellationToken);
    }
}
