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

namespace CanAppTemplate.Application.Features.Account.Commands.UnlinkExternalLogin;

/// <summary>
/// Bağlı dış hesabı kaldırır. Hesaba girmenin tek yolu buysa kaldırılmaz. Bağlama, sağlayıcıya yönlendirme
/// gerektirdiği için WebApi'deki <c>/api/auth/external/{provider}/link</c> ile yapılır.
/// </summary>
public sealed record UnlinkExternalLoginCommand(string Provider) : IRequest, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class UnlinkExternalLoginCommandHandler : IRequestHandler<UnlinkExternalLoginCommand>
{
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly ISecurityEvents _events;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public UnlinkExternalLoginCommandHandler(
        IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys,
        ISecurityEvents events,
        AuthBusinessRules authBusinessRules,
        AccountBusinessRules accountBusinessRules)
    {
        _passkeys = passkeys;
        _events = events;
        _authBusinessRules = authBusinessRules;
        _accountBusinessRules = accountBusinessRules;
    }

    public async Task Handle(UnlinkExternalLoginCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(q => q.Include(u => u.Logins), cancellationToken: cancellationToken);

        string provider = request.Provider.Trim().ToLowerInvariant();
        _accountBusinessRules.ExternalLoginShouldExist(user.Logins.Any(l => l.LoginProvider == provider));

        bool hasOtherWay = user.PasswordHash is not null
            || user.Logins.Count > 1
            || await _passkeys.AnyAsync(p => p.UserId == user.Id, cancellationToken: cancellationToken);
        _accountBusinessRules.SignInMethodShouldRemain(hasOtherWay);

        user.RemoveLogin(provider);
        await _events.PublishAsync(
            new SecurityEvent(SecurityEventTypes.ExternalLoginUnlinked, user.Id.ToString()) { Properties = new Dictionary<string, string?> { ["provider"] = provider } },
            cancellationToken
        );
    }
}
