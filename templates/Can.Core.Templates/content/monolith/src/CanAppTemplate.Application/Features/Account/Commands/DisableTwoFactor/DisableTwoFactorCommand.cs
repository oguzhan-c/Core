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
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.DisableTwoFactor;

/// <summary>İki adımlı doğrulamayı kapatır; güvenlik için şifre tekrar istenir. Kurtarma kodları silinir.</summary>
public sealed record DisableTwoFactorCommand(string Password) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class DisableTwoFactorCommandHandler : IRequestHandler<DisableTwoFactorCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<OtpAuthenticator<Guid>, Guid> _otpAuthenticators;
    private readonly Can.Core.Security.Hashing.IPasswordHasher _passwordHasher;
    private readonly RecoveryCodeIssuer _recoveryCodes;
    private readonly ISecurityEvents _events;

    public DisableTwoFactorCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<OtpAuthenticator<Guid>, Guid> otpAuthenticators,
        Can.Core.Security.Hashing.IPasswordHasher passwordHasher,
        RecoveryCodeIssuer recoveryCodes,
        ISecurityEvents events)
    {
        _currentUser = currentUser;
        _users = users;
        _otpAuthenticators = otpAuthenticators;
        _passwordHasher = passwordHasher;
        _recoveryCodes = recoveryCodes;
        _events = events;
    }

    public async Task<Result<Success>> Handle(DisableTwoFactorCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (user.PasswordHash is null || _passwordHasher.Verify(request.Password, user.PasswordHash) == Can.Core.Security.Hashing.PasswordVerificationResult.Failed)
            return AuthErrors.InvalidPassword;

        if (await _otpAuthenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken) is { } otp)
            _otpAuthenticators.Delete(otp, permanent: true);

        await _recoveryCodes.DeleteAllAsync(user.Id, cancellationToken);
        user.SetAuthenticator(AuthenticatorType.None);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.TwoFactorDisabled, user.Id.ToString()), cancellationToken);
        return Result.Success;
    }
}
