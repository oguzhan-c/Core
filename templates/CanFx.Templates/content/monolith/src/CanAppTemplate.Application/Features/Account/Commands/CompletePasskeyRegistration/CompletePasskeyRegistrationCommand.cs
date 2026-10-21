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

namespace CanAppTemplate.Application.Features.Account.Commands.CompletePasskeyRegistration;

/// <summary>Passkey kaydını tamamlar: tarayıcının yanıtı ilk adımdaki seçeneklerle doğrulanır ve passkey saklanır.</summary>
public sealed record CompletePasskeyRegistrationCommand(AuthenticatorAttestationRawResponse Response, CredentialCreateOptions Options, string? Name)
    : IRequest<PasskeyDto>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class CompletePasskeyRegistrationCommandHandler : IRequestHandler<CompletePasskeyRegistrationCommand, PasskeyDto>
{
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly IPasskeyService _passkeyService;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public CompletePasskeyRegistrationCommandHandler(
        IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys,
        IPasskeyService passkeyService,
        ISecurityEvents events,
        TimeProvider timeProvider,
        AuthBusinessRules authBusinessRules,
        AccountBusinessRules accountBusinessRules)
    {
        _passkeys = passkeys;
        _passkeyService = passkeyService;
        _events = events;
        _timeProvider = timeProvider;
        _authBusinessRules = authBusinessRules;
        _accountBusinessRules = accountBusinessRules;
    }

    public async Task<PasskeyDto> Handle(CompletePasskeyRegistrationCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(cancellationToken: cancellationToken);
        _accountBusinessRules.PasskeyOptionsShouldBelongToUser(request.Options.User.Id.SequenceEqual(user.PasskeyUserHandle));

        PasskeyCredential? credential = null;
        try
        {
            credential = await _passkeyService.CompleteRegistrationAsync(
                request.Response,
                request.Options,
                async (id, ct) => !await _passkeys.AnyAsync(p => p.CredentialId == id, cancellationToken: ct),
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fido2VerificationException (imza, challenge, origin ...) ya da bozuk yanıt: aşağıdaki kural reddeder.
        }

        _accountBusinessRules.PasskeyShouldBeRegistered(credential is not null);

        var passkey = new UserPasskey<IdentityKey>(user.Id, credential, request.Name ?? string.Empty, _timeProvider.GetUtcNow());
        await _passkeys.AddAsync(passkey, cancellationToken);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasskeyAdded, user.Id.ToString()), cancellationToken);
        return PasskeyDto.From(passkey);
    }
}
