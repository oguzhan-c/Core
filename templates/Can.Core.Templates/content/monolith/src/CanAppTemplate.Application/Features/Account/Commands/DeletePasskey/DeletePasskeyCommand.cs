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
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.DeletePasskey;

public sealed record DeletePasskeyCommand(IdentityKey Id) : IRequest, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class DeletePasskeyCommandHandler : IRequestHandler<DeletePasskeyCommand>
{
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly ISecurityEvents _events;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public DeletePasskeyCommandHandler(
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

    public async Task Handle(DeletePasskeyCommand request, CancellationToken cancellationToken)
    {
        IdentityKey userId = _authBusinessRules.CurrentUserIdShouldBeKnown();

        UserPasskey<IdentityKey>? passkey = await _passkeys.GetAsync(p => p.Id == request.Id && p.UserId == userId, cancellationToken: cancellationToken);
        _accountBusinessRules.PasskeyShouldExist(passkey is not null);

        // Hesaba girmenin tek yolu bu passkey ise silinmez.
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(q => q.Include(u => u.Logins), cancellationToken: cancellationToken);
        bool hasOtherWay = user.PasswordHash is not null
            || user.Logins.Count > 0
            || await _passkeys.AnyAsync(p => p.UserId == user.Id && p.Id != passkey.Id, cancellationToken: cancellationToken);
        _accountBusinessRules.SignInMethodShouldRemain(hasOtherWay);

        _passkeys.Delete(passkey, permanent: true);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasskeyRemoved, user.Id.ToString()), cancellationToken);
    }
}
