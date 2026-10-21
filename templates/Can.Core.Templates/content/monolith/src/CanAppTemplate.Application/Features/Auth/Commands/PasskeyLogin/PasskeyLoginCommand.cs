using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Passkeys;
using Can.Core.Security.Sessions;
using Fido2NetLib;
using FluentValidation;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.PasskeyLogin;

/// <summary>
/// Passkey ile giriş (şifresiz). Kullanıcı adı sorulmaz: tarayıcı/cihaz kayıtlı passkey'lerden birini önerir. Passkey
/// kendi başına çok faktörlü sayıldığı için (cihaz + biyometri/PIN) ayrıca ikinci adım istenmez.
/// </summary>
/// <param name="Options">Sunucunun ilk adımda ürettiği ve sakladığı seçenekler (challenge tek kullanımlık).</param>
public sealed record PasskeyLoginCommand(AuthenticatorAssertionRawResponse Response, AssertionOptions Options, ClientInfo Client)
    : IRequest<Result<AuthResult>>, ILoggableRequest;

public sealed class PasskeyLoginCommandHandler : IRequestHandler<PasskeyLoginCommand, Result<AuthResult>>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly IRepository<UserPasskey<IdentityKey>, IdentityKey> _passkeys;
    private readonly IPasskeyService _passkeyService;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public PasskeyLoginCommandHandler(
        IRepository<AppUser, IdentityKey> users,
        IRepository<UserPasskey<IdentityKey>, IdentityKey> passkeys,
        IPasskeyService passkeyService,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _users = users;
        _passkeys = passkeys;
        _passkeyService = passkeyService;
        _tokenIssuer = tokenIssuer;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AuthResult>> Handle(PasskeyLoginCommand request, CancellationToken cancellationToken)
    {
        byte[] credentialId = request.Response.RawId;
        UserPasskey<IdentityKey>? passkey = await _passkeys.GetAsync(p => p.CredentialId == credentialId, cancellationToken: cancellationToken);
        if (passkey is null)
            return AuthErrors.PasskeyFailed;

        AppUser? user = await _users.GetByIdAsync(passkey.UserId, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);
        if (user is null)
            return AuthErrors.PasskeyFailed;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (user.IsLockedOut(now))
            return AuthErrors.LockedOut;

        PasskeyAssertion assertion;
        try
        {
            assertion = await _passkeyService.CompleteLoginAsync(
                request.Response,
                request.Options,
                passkey.PublicKey,
                passkey.SignCount,
                (userHandle, id, _) => Task.FromResult(userHandle.SequenceEqual(user.PasskeyUserHandle) && id.SequenceEqual(passkey.CredentialId)),
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fido2VerificationException (imza, challenge, origin, sayaç ...) ya da bozuk yanıt.
            await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.LoginFailed, user.Id.ToString()) { Reason = "passkey_invalid" }, cancellationToken);
            return AuthErrors.PasskeyFailed;
        }

        if (!user.EmailConfirmed)
            return AuthErrors.EmailNotConfirmed;

        passkey.RecordUse(assertion.SignCount, now);
        user.ResetAccessFailed();

        AuthResult result = await AuthEvents.SignInAsync(user, request.Client, "passkey", _tokenIssuer, _sessions, _events, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
