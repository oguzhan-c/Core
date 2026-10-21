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

namespace CanAppTemplate.Application.Features.Account.Queries.GetAccountSecurity;

public sealed record GetAccountSecurityQuery : IRequest<Result<AccountSecurityDto>>, ISecuredRequest;

public sealed class GetAccountSecurityQueryHandler : IRequestHandler<GetAccountSecurityQuery, Result<AccountSecurityDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly IRepository<UserLogin<IdentityKey>, IdentityKey> _logins;
    private readonly IRepository<RecoveryCode<IdentityKey>, IdentityKey> _recoveryCodes;
    private readonly IServiceProvider _services;

    public GetAccountSecurityQueryHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, IdentityKey> users,
        IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys,
        IRepository<UserLogin<IdentityKey>, IdentityKey> logins,
        IRepository<RecoveryCode<IdentityKey>, IdentityKey> recoveryCodes,
        IServiceProvider services)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
        _logins = logins;
        _recoveryCodes = recoveryCodes;
        _services = services;
    }

    public async Task<Result<AccountSecurityDto>> Handle(GetAccountSecurityQuery request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;

        List<UserPasskey<IdentityKey>> passkeys = await _passkeys.Query(enableTracking: false).Where(p => p.UserId == user.Id).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        List<UserLogin<IdentityKey>> logins = await _logins.Query(enableTracking: false).Where(l => l.UserId == user.Id).OrderBy(l => l.LoginProvider).ToListAsync(cancellationToken);
        int recoveryLeft = await _recoveryCodes.CountAsync(c => c.UserId == user.Id && c.UsedAt == null, cancellationToken: cancellationToken);

        return new AccountSecurityDto(
            user.Email,
            user.EmailConfirmed,
            user.PasswordHash is not null,
            user.AuthenticatorType,
            recoveryLeft,
            _services.GetService<IPasskeyService>() is not null,
            passkeys.Select(PasskeyDto.From).ToList(),
            logins.Select(ExternalLoginDto.From).ToList()
        );
    }
}
