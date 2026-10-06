using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Passkeys;
using Fido2NetLib;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

/// <summary>
/// Passkey ile giriş (şifresiz). Kullanıcı adı sorulmaz: tarayıcı/cihaz kayıtlı passkey'lerden birini önerir,
/// passkey'in sahibi bulunur ve seçilen mağazaya ait olduğu doğrulanır. Passkey kendi başına çok faktörlü
/// sayıldığı için (cihaz + biyometri/PIN) ayrıca ikinci adım istenmez.
/// </summary>
/// <param name="Options">Sunucunun ilk adımda ürettiği ve sakladığı seçenekler (challenge tek kullanımlık).</param>
public sealed record PasskeyLoginCommand(string Tenant, AuthenticatorAssertionRawResponse Response, AssertionOptions Options, string? IpAddress = null)
    : IRequest<Result<AuthResult>>, ILoggableRequest;

public sealed class PasskeyLoginCommandValidator : AbstractValidator<PasskeyLoginCommand>
{
    public PasskeyLoginCommandValidator()
    {
        RuleFor(c => c.Tenant).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Response).NotNull();
        RuleFor(c => c.Options).NotNull();
    }
}

public sealed class PasskeyLoginCommandHandler : IRequestHandler<PasskeyLoginCommand, Result<AuthResult>>
{
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;
    private readonly IPasskeyService _passkeyService;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public PasskeyLoginCommandHandler(
        StoreTenant storeTenant,
        IRepository<AppUser, Guid> users,
        IRepository<UserPasskey<Guid>, Guid> passkeys,
        IPasskeyService passkeyService,
        AuthTokenIssuer tokenIssuer,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _storeTenant = storeTenant;
        _users = users;
        _passkeys = passkeys;
        _passkeyService = passkeyService;
        _tokenIssuer = tokenIssuer;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AuthResult>> Handle(PasskeyLoginCommand request, CancellationToken cancellationToken)
    {
        Result<Can.Core.MultiTenancy.TenantInfo> store = await _storeTenant.UseAsync(request.Tenant, cancellationToken);
        if (store.IsFailure)
            return store.Errors;

        byte[] credentialId = request.Response.RawId;
        UserPasskey<Guid>? passkey = await _passkeys.GetAsync(p => p.CredentialId == credentialId, cancellationToken: cancellationToken);
        if (passkey is null)
            return AuthErrors.PasskeyFailed;

        // Kullanıcı sorgusu tenant filtresinden geçer: passkey başka mağazanın kullanıcısına aitse bulunmaz.
        AppUser? user = await _users.GetByIdAsync(
            passkey.UserId,
            include: q => q.Include(u => u.UserRoles).ThenInclude(ur => ur.Role),
            cancellationToken: cancellationToken
        );
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
            return AuthErrors.PasskeyFailed;
        }

        if (!user.EmailConfirmed)
            return AuthErrors.EmailNotConfirmed;

        passkey.RecordUse(assertion.SignCount, now);
        user.ResetAccessFailed();

        AuthResult result = await _tokenIssuer.IssueAsync(user, request.IpAddress, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}
