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
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Queries.GetAccountSecurity;

public sealed record GetAccountSecurityQuery : IRequest<AccountSecurityDto>, ISecuredRequest;

public sealed class GetAccountSecurityQueryHandler : IRequestHandler<GetAccountSecurityQuery, AccountSecurityDto>
{
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly IRepository<UserLogin<IdentityKey>, IdentityKey> _logins;
    private readonly IRepository<RecoveryCode<IdentityKey>, IdentityKey> _recoveryCodes;
    private readonly IServiceProvider _services;
    private readonly AuthBusinessRules _authBusinessRules;

    public GetAccountSecurityQueryHandler(
        IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys,
        IRepository<UserLogin<IdentityKey>, IdentityKey> logins,
        IRepository<RecoveryCode<IdentityKey>, IdentityKey> recoveryCodes,
        IServiceProvider services,
        AuthBusinessRules authBusinessRules)
    {
        _passkeys = passkeys;
        _logins = logins;
        _recoveryCodes = recoveryCodes;
        _services = services;
        _authBusinessRules = authBusinessRules;
    }

    public async Task<AccountSecurityDto> Handle(GetAccountSecurityQuery request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(enableTracking: false, cancellationToken: cancellationToken);

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
