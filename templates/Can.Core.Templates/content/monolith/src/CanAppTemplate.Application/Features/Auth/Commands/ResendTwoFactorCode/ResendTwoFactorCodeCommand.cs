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
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ResendTwoFactorCode;

/// <summary>E-posta ile ikinci adımda yeni kod gönderir.</summary>
public sealed record ResendTwoFactorCodeCommand(TwoFactorChallenge Challenge) : IRequest<Result<Success>>, ITransactionalRequest;

public sealed class ResendTwoFactorCodeCommandHandler : IRequestHandler<ResendTwoFactorCodeCommand, Result<Success>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly EmailVerification _emailVerification;

    public ResendTwoFactorCodeCommandHandler(IRepository<AppUser, Guid> users, EmailVerification emailVerification)
    {
        _users = users;
        _emailVerification = emailVerification;
    }

    public async Task<Result<Success>> Handle(ResendTwoFactorCodeCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> user = await TwoFactorChallenges.ResolveUserAsync(_users, request.Challenge, cancellationToken);
        if (user.IsFailure)
            return user.Errors;

        if (user.Value.AuthenticatorType != AuthenticatorType.Email)
            return Error.Failure("two_factor_not_email", "Bu hesap kodları authenticator uygulamasından alıyor.");

        await _emailVerification.SendCodeAsync(user.Value, VerificationPurpose.SignIn, cancellationToken);
        return Result.Success;
    }
}
