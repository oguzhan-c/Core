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

namespace CanAppTemplate.Application.Features.Account.Commands.CompletePasskeyRegistration;

/// <summary>Passkey kaydını tamamlar: tarayıcının yanıtı ilk adımdaki seçeneklerle doğrulanır ve passkey saklanır.</summary>
public sealed record CompletePasskeyRegistrationCommand(AuthenticatorAttestationRawResponse Response, CredentialCreateOptions Options, string? Name)
    : IRequest<Result<PasskeyDto>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class CompletePasskeyRegistrationCommandHandler : IRequestHandler<CompletePasskeyRegistrationCommand, Result<PasskeyDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly IPasskeyService _passkeyService;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;

    public CompletePasskeyRegistrationCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, IdentityKey> users,
        IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys,
        IPasskeyService passkeyService,
        ISecurityEvents events,
        TimeProvider timeProvider)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
        _passkeyService = passkeyService;
        _events = events;
        _timeProvider = timeProvider;
    }

    public async Task<Result<PasskeyDto>> Handle(CompletePasskeyRegistrationCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;

        // Seçenekler bu kullanıcı için üretilmiş olmalı (başka oturumun seçenekleriyle kayıt yapılamaz).
        if (!request.Options.User.Id.SequenceEqual(user.PasskeyUserHandle))
            return Error.Forbidden("passkey.wrong_user", "Passkey isteği bu hesaba ait değil.");

        PasskeyCredential credential;
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
            return Error.Failure(AuthErrorCodes.PasskeyFailed, "Passkey kaydedilemedi; tekrar dene.");
        }

        var passkey = new UserPasskey<IdentityKey>(user.Id, credential, request.Name ?? string.Empty, _timeProvider.GetUtcNow());
        await _passkeys.AddAsync(passkey, cancellationToken);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasskeyAdded, user.Id.ToString()), cancellationToken);
        return PasskeyDto.From(passkey);
    }
}
