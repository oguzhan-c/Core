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

namespace CanAppTemplate.Application.Features.Account.Commands.BeginPasskeyRegistration;

/// <summary>Passkey kaydının ilk adımı: tarayıcıya verilecek seçenekler (WebApi bunları kısa süre saklar).</summary>
public sealed record BeginPasskeyRegistrationCommand : IRequest<CredentialCreateOptions>, ISecuredRequest;

public sealed class BeginPasskeyRegistrationCommandHandler : IRequestHandler<BeginPasskeyRegistrationCommand, CredentialCreateOptions>
{
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly IPasskeyService _passkeyService;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public BeginPasskeyRegistrationCommandHandler(
        IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys,
        IPasskeyService passkeyService,
        AuthBusinessRules authBusinessRules,
        AccountBusinessRules accountBusinessRules)
    {
        _passkeys = passkeys;
        _passkeyService = passkeyService;
        _authBusinessRules = authBusinessRules;
        _accountBusinessRules = accountBusinessRules;
    }

    public async Task<CredentialCreateOptions> Handle(BeginPasskeyRegistrationCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(cancellationToken: cancellationToken);

        List<byte[]> existing = await _passkeys.Query(enableTracking: false).Where(p => p.UserId == user.Id).Select(p => p.CredentialId).ToListAsync(cancellationToken);
        _accountBusinessRules.PasskeyLimitShouldNotBeExceeded(existing.Count);

        return _passkeyService.BeginRegistration(new PasskeyUser(user.PasskeyUserHandle, user.Email, user.FullName), existing);
    }
}
