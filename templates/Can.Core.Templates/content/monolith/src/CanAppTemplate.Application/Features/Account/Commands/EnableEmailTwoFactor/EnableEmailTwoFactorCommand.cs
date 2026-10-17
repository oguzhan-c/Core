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
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.EnableEmailTwoFactor;

public sealed record EnableEmailTwoFactorCommand : IRequest<Result<RecoveryCodesDto>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class EnableEmailTwoFactorCommandHandler : IRequestHandler<EnableEmailTwoFactorCommand, Result<RecoveryCodesDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<OtpAuthenticator<Guid>, Guid> _otpAuthenticators;
    private readonly RecoveryCodeIssuer _recoveryCodes;
    private readonly ISecurityEvents _events;

    public EnableEmailTwoFactorCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<OtpAuthenticator<Guid>, Guid> otpAuthenticators,
        RecoveryCodeIssuer recoveryCodes,
        ISecurityEvents events)
    {
        _currentUser = currentUser;
        _users = users;
        _otpAuthenticators = otpAuthenticators;
        _recoveryCodes = recoveryCodes;
        _events = events;
    }

    public async Task<Result<RecoveryCodesDto>> Handle(EnableEmailTwoFactorCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (!user.EmailConfirmed)
            return Error.Failure("email_not_confirmed", "Önce e-posta adresini doğrula.");

        if (await _otpAuthenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken) is { } otp)
            _otpAuthenticators.Delete(otp, permanent: true);

        user.SetAuthenticator(AuthenticatorType.Email);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.TwoFactorEnabled, user.Id.ToString()) { Reason = "email" }, cancellationToken);
        return await _recoveryCodes.RegenerateAsync(user.Id, cancellationToken);
    }
}
