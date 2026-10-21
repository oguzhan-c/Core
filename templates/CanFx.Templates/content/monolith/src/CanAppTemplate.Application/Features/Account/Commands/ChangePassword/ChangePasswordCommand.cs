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
using CanAppTemplate.Application.Features.Account.Commands.ReissueCurrentSession;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.ChangePassword;

/// <summary>
/// Şifre değiştirir ya da (dış sağlayıcıyla açılmış, şifresiz hesapta) şifre belirler. Güvenlik damgası yenilenir:
/// diğer cihazlardaki oturumlar kapanır. Bu oturum WebApi'de yeni token'larla sürer (<see cref="ReissueCurrentSessionCommand"/>).
/// </summary>
public sealed record ChangePasswordCommand(string? CurrentPassword, string NewPassword) : IRequest, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand>
{
    private readonly CanFx.Security.Hashing.IPasswordHasher _passwordHasher;
    private readonly ISecurityEvents _events;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public ChangePasswordCommandHandler(
        CanFx.Security.Hashing.IPasswordHasher passwordHasher,
        ISecurityEvents events,
        AuthBusinessRules authBusinessRules,
        AccountBusinessRules accountBusinessRules)
    {
        _passwordHasher = passwordHasher;
        _events = events;
        _authBusinessRules = authBusinessRules;
        _accountBusinessRules = accountBusinessRules;
    }

    public async Task Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(cancellationToken: cancellationToken);

        // Şifresi olmayan hesap (dış sağlayıcıyla açılmış) ilk şifresini mevcut şifre sormadan belirler.
        _accountBusinessRules.CurrentPasswordShouldMatch(
            user.PasswordHash is null
            || (request.CurrentPassword is not null
                && _passwordHasher.Verify(request.CurrentPassword, user.PasswordHash) != CanFx.Security.Hashing.PasswordVerificationResult.Failed));

        user.SetPasswordHash(_passwordHasher.Hash(request.NewPassword));
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasswordChanged, user.Id.ToString()), cancellationToken);
    }
}
