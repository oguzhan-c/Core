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

namespace CanAppTemplate.Application.Features.Account.Commands.EnableOtp;

/// <summary>
/// Kurulumu tamamlar: uygulamadaki ilk kod doğruysa girişte artık bu uygulamanın kodu istenir. Kurtarma kodları
/// üretilip bir kez döndürülür.
/// </summary>
public sealed record EnableOtpCommand(string Code) : IRequest<Result<RecoveryCodesDto>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class EnableOtpCommandHandler : IRequestHandler<EnableOtpCommand, Result<RecoveryCodesDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly TwoFactorCodeVerifier _verifier;
    private readonly RecoveryCodeIssuer _recoveryCodes;
    private readonly ISecurityEvents _events;

    public EnableOtpCommandHandler(ICurrentUser currentUser, IRepository<AppUser, IdentityKey> users, TwoFactorCodeVerifier verifier, RecoveryCodeIssuer recoveryCodes, ISecurityEvents events)
    {
        _currentUser = currentUser;
        _users = users;
        _verifier = verifier;
        _recoveryCodes = recoveryCodes;
        _events = events;
    }

    public async Task<Result<RecoveryCodesDto>> Handle(EnableOtpCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (await _verifier.VerifyOtpAsync(user.Id, request.Code, requireVerified: false, cancellationToken) is null)
            return Error.Failure(AuthErrorCodes.InvalidTwoFactorCode, "Kod hatalı. Uygulamadaki güncel kodu gir (saatin doğru olduğundan emin ol).");

        user.SetAuthenticator(AuthenticatorType.Otp);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.TwoFactorEnabled, user.Id.ToString()) { Reason = "otp" }, cancellationToken);
        return await _recoveryCodes.RegenerateAsync(user.Id, cancellationToken);
    }
}
