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

namespace CanAppTemplate.Application.Features.Account.Commands.UnlinkExternalLogin;

/// <summary>
/// Bağlı dış hesabı kaldırır. Hesaba girmenin tek yolu buysa kaldırılmaz. Bağlama, sağlayıcıya yönlendirme
/// gerektirdiği için WebApi'deki <c>/api/auth/external/{provider}/link</c> ile yapılır.
/// </summary>
public sealed record UnlinkExternalLoginCommand(string Provider) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class UnlinkExternalLoginCommandHandler : IRequestHandler<UnlinkExternalLoginCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly ISecurityEvents _events;

    public UnlinkExternalLoginCommandHandler(ICurrentUser currentUser, IRepository<AppUser, IdentityKey> users, IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys, ISecurityEvents events)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
        _events = events;
    }

    public async Task<Result<Success>> Handle(UnlinkExternalLoginCommand request, CancellationToken cancellationToken)
    {
        Result<IdentityKey> userId = _currentUser.UserId();
        if (userId.IsFailure)
            return userId.Errors;

        AppUser? user = await _users.GetAsync(u => u.Id == userId.Value, include: q => q.Include(u => u.Logins), cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        string provider = request.Provider.Trim().ToLowerInvariant();
        if (user.Logins.All(l => l.LoginProvider != provider))
            return AuthErrors.ExternalLoginNotFound;

        bool hasOtherWay = user.PasswordHash is not null
            || user.Logins.Count > 1
            || await _passkeys.AnyAsync(p => p.UserId == user.Id, cancellationToken: cancellationToken);
        if (!hasOtherWay)
            return AuthErrors.LastSignInMethod;

        user.RemoveLogin(provider);
        await _events.PublishAsync(
            new SecurityEvent(SecurityEventTypes.ExternalLoginUnlinked, user.Id.ToString()) { Properties = new Dictionary<string, string?> { ["provider"] = provider } },
            cancellationToken
        );
        return Result.Success;
    }
}
