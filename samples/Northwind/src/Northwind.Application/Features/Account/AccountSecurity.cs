using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Hashing;
using Can.Core.Security.Otp;
using Can.Core.Security.Passkeys;
using Fido2NetLib;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Northwind.Application.Features.Auth;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Account;

// ---------------------------------------------------------------- modeller

public sealed record PasskeyDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool IsBackedUp)
{
    public static PasskeyDto From(UserPasskey<Guid> passkey) =>
        new(passkey.Id, passkey.Name, passkey.CreatedAt, passkey.LastUsedAt, passkey.IsBackedUp);
}

/// <summary>Hesaba bağlı dış hesap (Google, Microsoft, GitHub ...).</summary>
public sealed record ExternalLoginDto(string Provider, string? DisplayName, DateTimeOffset CreatedAt)
{
    public static ExternalLoginDto From(UserLogin<Guid> login) => new(login.LoginProvider, login.ProviderDisplayName, login.CreatedAt);
}

/// <summary>Hesap güvenliği sayfasının özeti.</summary>
/// <param name="HasPassword">Şifresi var mı (dış sağlayıcıyla açılan hesapların şifresi olmayabilir).</param>
/// <param name="TwoFactor">Girişte şifreden sonra istenen ikinci adım (<c>None</c>, <c>Email</c>, <c>Otp</c>).</param>
/// <param name="PasskeysEnabled">Sunucuda passkey ayarlı mı (<c>Security:Passkey</c>).</param>
public sealed record AccountSecurityDto(
    string Email,
    bool EmailConfirmed,
    bool HasPassword,
    AuthenticatorType TwoFactor,
    bool PasskeysEnabled,
    IReadOnlyList<PasskeyDto> Passkeys,
    IReadOnlyList<ExternalLoginDto> ExternalLogins);

/// <summary>Authenticator uygulamasına eklenecek bilgiler: QR kodu (<see cref="ProvisioningUri"/>) ya da elle girilecek anahtar.</summary>
public sealed record OtpSetupDto(string Secret, string ProvisioningUri);

internal static class AccountUsers
{
    /// <summary>Giriş yapmış kullanıcıyı (bu mağazada) yükler.</summary>
    public static async Task<Result<AppUser>> CurrentAsync(ICurrentUser currentUser, IRepository<AppUser, Guid> users, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(currentUser.Id, out Guid userId))
            return Error.Unauthorized();

        return await users.GetByIdAsync(userId, cancellationToken: cancellationToken).ToResult(Error.Unauthorized());
    }

    public static Result<Guid> CurrentId(ICurrentUser currentUser) =>
        Guid.TryParse(currentUser.Id, out Guid userId) ? userId : Error.Unauthorized();

    public static readonly Error PasskeyNotFound = Error.NotFound("passkey.not_found", "Passkey bulunamadı.");
}

// ---------------------------------------------------------------- özet

public sealed record GetAccountSecurityQuery : IRequest<Result<AccountSecurityDto>>, ISecuredRequest;

public sealed class GetAccountSecurityQueryHandler : IRequestHandler<GetAccountSecurityQuery, Result<AccountSecurityDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;
    private readonly IRepository<UserLogin<Guid>, Guid> _logins;
    private readonly IServiceProvider _services;

    public GetAccountSecurityQueryHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<UserPasskey<Guid>, Guid> passkeys,
        IRepository<UserLogin<Guid>, Guid> logins,
        IServiceProvider services)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
        _logins = logins;
        _services = services;
    }

    public async Task<Result<AccountSecurityDto>> Handle(GetAccountSecurityQuery request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;

        List<UserPasskey<Guid>> passkeys = await _passkeys
            .Query(enableTracking: false)
            .Where(p => p.UserId == user.Id)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        List<UserLogin<Guid>> logins = await _logins
            .Query(enableTracking: false)
            .Where(l => l.UserId == user.Id)
            .OrderBy(l => l.LoginProvider)
            .ToListAsync(cancellationToken);

        return new AccountSecurityDto(
            user.Email,
            user.EmailConfirmed,
            user.PasswordHash is not null,
            user.AuthenticatorType,
            _services.GetService<IPasskeyService>() is not null,
            passkeys.Select(PasskeyDto.From).ToList(),
            logins.Select(ExternalLoginDto.From).ToList()
        );
    }
}

// ---------------------------------------------------------------- authenticator uygulaması (TOTP)

/// <summary>
/// Authenticator uygulaması kurulumunun ilk adımı: yeni gizli anahtar üretilir (henüz aktif değil). Kullanıcı QR kodu
/// okutup uygulamadaki ilk kodu <see cref="EnableOtpCommand"/> ile girince iki adımlı giriş açılır.
/// </summary>
public sealed record BeginOtpSetupCommand : IRequest<Result<OtpSetupDto>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class BeginOtpSetupCommandHandler : IRequestHandler<BeginOtpSetupCommand, Result<OtpSetupDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<OtpAuthenticator<Guid>, Guid> _otpAuthenticators;
    private readonly ITotpService _totp;
    private readonly TenantContext _tenantContext;
    private readonly IUnitOfWork _unitOfWork;

    public BeginOtpSetupCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<OtpAuthenticator<Guid>, Guid> otpAuthenticators,
        ITotpService totp,
        TenantContext tenantContext,
        IUnitOfWork unitOfWork)
    {
        _currentUser = currentUser;
        _users = users;
        _otpAuthenticators = otpAuthenticators;
        _totp = totp;
        _tenantContext = tenantContext;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<OtpSetupDto>> Handle(BeginOtpSetupCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (user.AuthenticatorType == AuthenticatorType.Otp)
            return Error.Conflict("otp.already_enabled", "Authenticator uygulaması zaten açık. Yenisini kurmak için önce iki adımlı doğrulamayı kapat.");

        // Yarım kalmış eski kurulum varsa silinir (kullanıcı başına tek kayıt; benzersiz index).
        if (await _otpAuthenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken) is { } existing)
        {
            _otpAuthenticators.Delete(existing, permanent: true);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        byte[] secret = _totp.GenerateSecret();
        await _otpAuthenticators.AddAsync(new OtpAuthenticator<Guid>(user.Id, secret), cancellationToken);

        string issuer = _tenantContext.Tenant?.Name is { Length: > 0 } store ? $"Northwind {store}".Replace(':', ' ') : "Northwind";
        string base32 = Base32.Encode(secret);

        return new OtpSetupDto(
            string.Join(' ', base32.Chunk(4).Select(c => new string(c))), // elle girerken okunur olsun: ABCD EFGH ...
            _totp.GetProvisioningUri(secret, issuer, user.Email)
        );
    }
}

/// <summary>Kurulumu tamamlar: uygulamadaki ilk kod doğruysa girişte artık bu uygulamanın kodu istenir.</summary>
public sealed record EnableOtpCommand(string Code) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class EnableOtpCommandValidator : AbstractValidator<EnableOtpCommand>
{
    public EnableOtpCommandValidator()
    {
        RuleFor(c => c.Code).NotEmpty().Matches("^[0-9]{6}$").WithMessage("Kod 6 haneli olmalı.");
    }
}

public sealed class EnableOtpCommandHandler : IRequestHandler<EnableOtpCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly TwoFactorCodeVerifier _verifier;

    public EnableOtpCommandHandler(ICurrentUser currentUser, IRepository<AppUser, Guid> users, TwoFactorCodeVerifier verifier)
    {
        _currentUser = currentUser;
        _users = users;
        _verifier = verifier;
    }

    public async Task<Result<Success>> Handle(EnableOtpCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (await _verifier.VerifyOtpAsync(user.Id, request.Code, requireVerified: false, cancellationToken) is null)
            return Error.Failure(AuthErrorCodes.InvalidTwoFactorCode, "Kod hatalı. Uygulamadaki güncel kodu gir (saatin doğru olduğundan emin ol).");

        user.SetAuthenticator(AuthenticatorType.Otp);
        return Result.Success;
    }
}

// ---------------------------------------------------------------- e-posta ile ikinci adım

public sealed record EnableEmailTwoFactorCommand : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class EnableEmailTwoFactorCommandHandler : IRequestHandler<EnableEmailTwoFactorCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<OtpAuthenticator<Guid>, Guid> _otpAuthenticators;

    public EnableEmailTwoFactorCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<OtpAuthenticator<Guid>, Guid> otpAuthenticators)
    {
        _currentUser = currentUser;
        _users = users;
        _otpAuthenticators = otpAuthenticators;
    }

    public async Task<Result<Success>> Handle(EnableEmailTwoFactorCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (!user.EmailConfirmed)
            return Error.Failure("email_not_confirmed", "Önce e-posta adresini doğrula.");

        if (await _otpAuthenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken) is { } otp)
            _otpAuthenticators.Delete(otp, permanent: true);

        user.SetAuthenticator(AuthenticatorType.Email);
        return Result.Success;
    }
}

// ---------------------------------------------------------------- kapatma

/// <summary>İki adımlı doğrulamayı kapatır; güvenlik için şifre tekrar istenir.</summary>
public sealed record DisableTwoFactorCommand(string Password) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class DisableTwoFactorCommandValidator : AbstractValidator<DisableTwoFactorCommand>
{
    public DisableTwoFactorCommandValidator()
    {
        RuleFor(c => c.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class DisableTwoFactorCommandHandler : IRequestHandler<DisableTwoFactorCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<OtpAuthenticator<Guid>, Guid> _otpAuthenticators;
    private readonly IPasswordHasher _passwordHasher;

    public DisableTwoFactorCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<OtpAuthenticator<Guid>, Guid> otpAuthenticators,
        IPasswordHasher passwordHasher)
    {
        _currentUser = currentUser;
        _users = users;
        _otpAuthenticators = otpAuthenticators;
        _passwordHasher = passwordHasher;
    }

    public async Task<Result<Success>> Handle(DisableTwoFactorCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (user.PasswordHash is null || _passwordHasher.Verify(request.Password, user.PasswordHash) == PasswordVerificationResult.Failed)
            return Error.Validation("invalid_password", "Şifre hatalı.", "Password");

        if (await _otpAuthenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken) is { } otp)
            _otpAuthenticators.Delete(otp, permanent: true);

        user.SetAuthenticator(AuthenticatorType.None);
        return Result.Success;
    }
}

// ---------------------------------------------------------------- passkey'ler

/// <summary>Passkey kaydının ilk adımı: tarayıcıya verilecek seçenekler (WebApi bunları kısa süre saklar).</summary>
public sealed record BeginPasskeyRegistrationCommand : IRequest<Result<CredentialCreateOptions>>, ISecuredRequest;

public sealed class BeginPasskeyRegistrationCommandHandler : IRequestHandler<BeginPasskeyRegistrationCommand, Result<CredentialCreateOptions>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;
    private readonly IPasskeyService _passkeyService;
    private readonly TenantContext _tenantContext;

    public BeginPasskeyRegistrationCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<UserPasskey<Guid>, Guid> passkeys,
        IPasskeyService passkeyService,
        TenantContext tenantContext)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
        _passkeyService = passkeyService;
        _tenantContext = tenantContext;
    }

    public async Task<Result<CredentialCreateOptions>> Handle(BeginPasskeyRegistrationCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;

        List<byte[]> existing = await _passkeys
            .Query(enableTracking: false)
            .Where(p => p.UserId == user.Id)
            .Select(p => p.CredentialId)
            .ToListAsync(cancellationToken);

        if (existing.Count >= PasskeyLimits.MaxPerUser)
            return Error.Failure("passkey.limit", $"En fazla {PasskeyLimits.MaxPerUser} passkey eklenebilir; önce birini sil.");

        // Cihazın passkey listesinde görünen ad: aynı e-posta farklı mağazalarda kullanılabildiği için mağaza adı da eklenir.
        string store = _tenantContext.Tenant?.Name ?? "Northwind";
        return _passkeyService.BeginRegistration(
            new PasskeyUser(user.PasskeyUserHandle, user.Email, $"{user.FirstName} {user.LastName} ({store})"),
            existing
        );
    }
}

public static class PasskeyLimits
{
    public const int MaxPerUser = 10;
    public const int NameMaxLength = 64;
}

/// <summary>Passkey kaydını tamamlar: tarayıcının yanıtı ilk adımdaki seçeneklerle doğrulanır ve passkey saklanır.</summary>
public sealed record CompletePasskeyRegistrationCommand(AuthenticatorAttestationRawResponse Response, CredentialCreateOptions Options, string? Name)
    : IRequest<Result<PasskeyDto>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class CompletePasskeyRegistrationCommandValidator : AbstractValidator<CompletePasskeyRegistrationCommand>
{
    public CompletePasskeyRegistrationCommandValidator()
    {
        RuleFor(c => c.Response).NotNull();
        RuleFor(c => c.Options).NotNull();
        RuleFor(c => c.Name).MaximumLength(PasskeyLimits.NameMaxLength);
    }
}

public sealed class CompletePasskeyRegistrationCommandHandler : IRequestHandler<CompletePasskeyRegistrationCommand, Result<PasskeyDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;
    private readonly IPasskeyService _passkeyService;
    private readonly TimeProvider _timeProvider;

    public CompletePasskeyRegistrationCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<UserPasskey<Guid>, Guid> passkeys,
        IPasskeyService passkeyService,
        TimeProvider timeProvider)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
        _passkeyService = passkeyService;
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

        var passkey = new UserPasskey<Guid>(user.Id, credential, request.Name ?? string.Empty, _timeProvider.GetUtcNow());
        await _passkeys.AddAsync(passkey, cancellationToken);
        return PasskeyDto.From(passkey);
    }
}

public sealed record RenamePasskeyCommand(Guid Id, string Name) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest;

public sealed class RenamePasskeyCommandValidator : AbstractValidator<RenamePasskeyCommand>
{
    public RenamePasskeyCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(PasskeyLimits.NameMaxLength);
    }
}

public sealed class RenamePasskeyCommandHandler : IRequestHandler<RenamePasskeyCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;

    public RenamePasskeyCommandHandler(ICurrentUser currentUser, IRepository<UserPasskey<Guid>, Guid> passkeys)
    {
        _currentUser = currentUser;
        _passkeys = passkeys;
    }

    public Task<Result<Success>> Handle(RenamePasskeyCommand request, CancellationToken cancellationToken) =>
        AccountUsers
            .CurrentId(_currentUser)
            .ThenAsync(userId =>
                _passkeys
                    .GetAsync(p => p.Id == request.Id && p.UserId == userId, cancellationToken: cancellationToken)
                    .ToResult(AccountUsers.PasskeyNotFound)
            )
            .Map(passkey =>
            {
                passkey.Rename(request.Name);
                return Result.Success;
            });
}

public sealed record DeletePasskeyCommand(Guid Id) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class DeletePasskeyCommandHandler : IRequestHandler<DeletePasskeyCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;

    public DeletePasskeyCommandHandler(ICurrentUser currentUser, IRepository<UserPasskey<Guid>, Guid> passkeys)
    {
        _currentUser = currentUser;
        _passkeys = passkeys;
    }

    public Task<Result<Success>> Handle(DeletePasskeyCommand request, CancellationToken cancellationToken) =>
        AccountUsers
            .CurrentId(_currentUser)
            .ThenAsync(userId =>
                _passkeys
                    .GetAsync(p => p.Id == request.Id && p.UserId == userId, cancellationToken: cancellationToken)
                    .ToResult(AccountUsers.PasskeyNotFound)
            )
            .Map(passkey =>
            {
                _passkeys.Delete(passkey, permanent: true);
                return Result.Success;
            });
}

// ---------------------------------------------------------------- dış hesaplar

/// <summary>
/// Bağlı dış hesabı kaldırır. Hesaba girmenin tek yolu buysa (şifre, passkey ya da başka dış hesap yok) kaldırılmaz.
/// Bağlama, sağlayıcıya yönlendirme gerektirdiği için WebApi'deki <c>/api/auth/external/{provider}/link</c> ile yapılır.
/// </summary>
public sealed record UnlinkExternalLoginCommand(string Provider) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class UnlinkExternalLoginCommandHandler : IRequestHandler<UnlinkExternalLoginCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;

    public UnlinkExternalLoginCommandHandler(ICurrentUser currentUser, IRepository<AppUser, Guid> users, IRepository<UserPasskey<Guid>, Guid> passkeys)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
    }

    public async Task<Result<Success>> Handle(UnlinkExternalLoginCommand request, CancellationToken cancellationToken)
    {
        Result<Guid> userId = AccountUsers.CurrentId(_currentUser);
        if (userId.IsFailure)
            return userId.Errors;

        AppUser? user = await _users.GetAsync(u => u.Id == userId.Value, include: q => q.Include(u => u.Logins), cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        string provider = request.Provider.Trim().ToLowerInvariant();
        if (user.Logins.All(l => l.LoginProvider != provider))
            return AuthErrors.ExternalLoginNotFound;

        bool hasOtherWay =
            user.PasswordHash is not null
            || user.Logins.Count > 1
            || await _passkeys.AnyAsync(p => p.UserId == user.Id, cancellationToken: cancellationToken);
        if (!hasOtherWay)
            return AuthErrors.LastSignInMethod;

        user.RemoveLogin(provider);
        return Result.Success;
    }
}
