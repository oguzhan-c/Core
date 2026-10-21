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

namespace CanAppTemplate.Application.Features.Account.Commands.DeletePasskey;

public sealed record DeletePasskeyCommand(IdentityKey Id) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class DeletePasskeyCommandHandler : IRequestHandler<DeletePasskeyCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly ISecurityEvents _events;

    public DeletePasskeyCommandHandler(ICurrentUser currentUser, IRepository<AppUser, IdentityKey> users, IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys, ISecurityEvents events)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
        _events = events;
    }

    public async Task<Result<Success>> Handle(DeletePasskeyCommand request, CancellationToken cancellationToken)
    {
        Result<IdentityKey> userId = _currentUser.UserId();
        if (userId.IsFailure)
            return userId.Errors;

        UserPasskey<IdentityKey>? passkey = await _passkeys.GetAsync(p => p.Id == request.Id && p.UserId == userId.Value, cancellationToken: cancellationToken);
        if (passkey is null)
            return AccountUsers.PasskeyNotFound;

        // Hesaba girmenin tek yolu bu passkey ise silinmez.
        AppUser? user = await _users.GetAsync(u => u.Id == userId.Value, include: q => q.Include(u => u.Logins), cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        bool hasOtherWay = user.PasswordHash is not null
            || user.Logins.Count > 0
            || await _passkeys.AnyAsync(p => p.UserId == user.Id && p.Id != passkey.Id, cancellationToken: cancellationToken);
        if (!hasOtherWay)
            return AuthErrors.LastSignInMethod;

        _passkeys.Delete(passkey, permanent: true);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasskeyRemoved, user.Id.ToString()), cancellationToken);
        return Result.Success;
    }
}
