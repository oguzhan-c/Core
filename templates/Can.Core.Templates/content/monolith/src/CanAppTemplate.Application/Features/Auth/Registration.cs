using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mailing;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Hashing;
using Can.Core.Security.Sessions;
using Can.Core.Security.VerificationCodes;
using FluentValidation;
using CanAppTemplate.Application.Common;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth;

// ---------------------------------------------------------------- kayıt

/// <summary>
/// Üye ol: müşteri hesabı açılır ve e-postaya 6 haneli doğrulama kodu gönderilir. E-posta doğrulanana kadar giriş
/// yapılamaz.
/// </summary>
public sealed record RegisterCommand(string Email, string Password, string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent)
    : IRequest<Result<RegisterResult>>, ITransactionalRequest, ILoggableRequest;

public sealed record RegisterResult(string Email, DateTimeOffset CodeExpiresAt);

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.Password).StrongPassword();
        RuleFor(c => c.FirstName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.LastName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.PhoneNumber).MaximumLength(AppUser.PhoneMaxLength);
    }
}

public sealed class RegisterCommandHandler : IRequestHandler<RegisterCommand, Result<RegisterResult>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<Role<Guid>, Guid> _roles;
    private readonly IPasswordHasher _passwordHasher;
    private readonly EmailVerification _emailVerification;

    public RegisterCommandHandler(IRepository<AppUser, Guid> users, IRepository<Role<Guid>, Guid> roles, IPasswordHasher passwordHasher, EmailVerification emailVerification)
    {
        _users = users;
        _roles = roles;
        _passwordHasher = passwordHasher;
        _emailVerification = emailVerification;
    }

    public async Task<Result<RegisterResult>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        if (await _users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, withDeleted: true, cancellationToken: cancellationToken))
            return AuthErrors.EmailTaken;

        Result<AppUser> registered = AppUser.Register(
            request.Email,
            request.FirstName,
            request.LastName,
            _passwordHasher.Hash(request.Password),
            request.MarketingConsent
        );
        if (registered.IsFailure)
            return registered.Errors;

        AppUser user = registered.Value;
        user.UpdateProfile(user.FirstName, user.LastName, request.PhoneNumber, request.MarketingConsent);
        user.AddRole(await DefaultRoles.GetAsync(_roles, cancellationToken));
        await _users.AddAsync(user, cancellationToken);

        DateTimeOffset expiresAt = await _emailVerification.SendCodeAsync(user, VerificationPurpose.ConfirmEmail, cancellationToken);
        return new RegisterResult(user.Email, expiresAt);
    }
}

internal static class DefaultRoles
{
    /// <summary>Seed eksikse bu bir kurulum hatasıdır (beklenmeyen): exception olarak kalır.</summary>
    public static async Task<Role<Guid>> GetAsync(IRepository<Role<Guid>, Guid> roles, CancellationToken cancellationToken) =>
        await roles.GetAsync(r => r.Name == Roles.User, cancellationToken: cancellationToken)
        ?? throw new InvalidOperationException("User rolü bulunamadı; seed çalıştı mı?");
}

// ---------------------------------------------------------------- doğrulama

/// <summary>E-postadaki kodu doğrular; başarılıysa kullanıcı doğrudan giriş yapmış olur.</summary>
public sealed record VerifyEmailCommand(string Email, string Code, ClientInfo Client) : IRequest<Result<AuthResult>>, ILoggableRequest;

public sealed class VerifyEmailCommandValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailCommandValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress();
        RuleFor(c => c.Code).SixDigitCode();
    }
}

public sealed class VerifyEmailCommandHandler : IRequestHandler<VerifyEmailCommand, Result<AuthResult>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly EmailVerification _emailVerification;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;

    public VerifyEmailCommandHandler(
        IRepository<AppUser, Guid> users,
        EmailVerification emailVerification,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork)
    {
        _users = users;
        _emailVerification = emailVerification;
        _tokenIssuer = tokenIssuer;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<AuthResult>> Handle(VerifyEmailCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);
        if (user is null)
            return AuthErrors.InvalidVerificationCode;

        // Doğrulanmış hesapta bu uç nokta şifresiz giriş kapısı olamaz (2FA atlanırdı): giriş sayfasına yönlendirilir.
        if (user.EmailConfirmed)
            return Error.Conflict("email_already_confirmed", "E-posta adresin zaten doğrulanmış; giriş yapabilirsin.");

        if (!await _emailVerification.VerifyAsync(user, request.Code, cancellationToken))
        {
            // Başarısız deneme sayacı kalıcı olsun (kaba kuvvet denemelerine karşı).
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return AuthErrors.InvalidVerificationCode;
        }

        user.ConfirmEmail();

        AuthResult result = await AuthEvents.SignInAsync(user, request.Client, "email_verification", _tokenIssuer, _sessions, _events, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>Yeni doğrulama kodu gönderir. Hesabın var olup olmadığını belli etmemek için her zaman başarılı döner.</summary>
public sealed record ResendVerificationCodeCommand(string Email) : IRequest<Result<Success>>, ITransactionalRequest;

public sealed class ResendVerificationCodeCommandHandler : IRequestHandler<ResendVerificationCodeCommand, Result<Success>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly EmailVerification _emailVerification;

    public ResendVerificationCodeCommandHandler(IRepository<AppUser, Guid> users, EmailVerification emailVerification)
    {
        _users = users;
        _emailVerification = emailVerification;
    }

    public async Task<Result<Success>> Handle(ResendVerificationCodeCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken: cancellationToken);

        if (user is { EmailConfirmed: false })
            await _emailVerification.SendCodeAsync(user, VerificationPurpose.ConfirmEmail, cancellationToken);

        return Result.Success;
    }
}

// ---------------------------------------------------------------- e-posta kodları

public enum VerificationPurpose
{
    ConfirmEmail = 0,
    SignIn = 1,
    ResetPassword = 2,
}

/// <summary>
/// E-postaya gönderilen 6 haneli kodlar (doğrulama, iki adımlı giriş, şifre sıfırlama). Kod HMAC'lenmiş olarak
/// <see cref="EmailAuthenticator{TId}"/>'da saklanır, 10 dakika geçerlidir ve en fazla 5 kez denenebilir.
/// </summary>
public sealed class EmailVerification
{
    private readonly IRepository<EmailAuthenticator<Guid>, Guid> _authenticators;
    private readonly IVerificationCodeService _codes;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _timeProvider;

    public EmailVerification(
        IRepository<EmailAuthenticator<Guid>, Guid> authenticators,
        IVerificationCodeService codes,
        IEmailSender emailSender,
        TimeProvider timeProvider)
    {
        _authenticators = authenticators;
        _codes = codes;
        _emailSender = emailSender;
        _timeProvider = timeProvider;
    }

    public async Task<DateTimeOffset> SendCodeAsync(AppUser user, VerificationPurpose purpose, CancellationToken cancellationToken)
    {
        EmailAuthenticator<Guid> authenticator = await GetOrCreateAsync(user, cancellationToken);
        VerificationCode code = _codes.Generate();
        authenticator.SetCode(code);

        (string subject, string reason) = purpose switch
        {
            VerificationPurpose.SignIn => ($"Giriş kodun: {code.Code}", $"{AppInfo.Name} hesabına giriş yapmak için"),
            VerificationPurpose.ResetPassword => ($"Şifre sıfırlama kodun: {code.Code}", "şifreni sıfırlamak için"),
            _ => ($"Doğrulama kodun: {code.Code}", $"{AppInfo.Name} hesabını doğrulamak için"),
        };

        string warning = purpose == VerificationPurpose.ConfirmEmail
            ? "Bu isteği sen yapmadıysan bu e-postayı yok sayabilirsin."
            : "Bu isteği sen yapmadıysan şifreni değiştirmeni öneririz.";

        var message = new EmailMessage(subject)
        {
            TextBody = $"Merhaba {user.FirstName},\n\n{reason} kodun: {code.Code}\n\nKod 10 dakika geçerlidir. {warning}",
            HtmlBody =
                $"<p>Merhaba {System.Net.WebUtility.HtmlEncode(user.FirstName)},</p>"
                + $"<p>{System.Net.WebUtility.HtmlEncode(reason)} kodun:</p>"
                + $"<p style=\"font-size:28px;font-weight:bold;letter-spacing:6px\">{code.Code}</p>"
                + $"<p>Kod 10 dakika geçerlidir. {System.Net.WebUtility.HtmlEncode(warning)}</p>",
        };
        message.To.Add(new EmailAddress(user.Email, user.FullName));
        await _emailSender.SendAsync(message, cancellationToken);

        return code.ExpiresAt;
    }

    public async Task<bool> VerifyAsync(AppUser user, string code, CancellationToken cancellationToken)
    {
        EmailAuthenticator<Guid>? authenticator = await _authenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken);
        if (authenticator is null || !authenticator.CanAttempt(_timeProvider.GetUtcNow()))
            return false;

        if (!_codes.Verify(code, authenticator.CodeHash!))
        {
            authenticator.RegisterFailedAttempt();
            return false;
        }

        authenticator.MarkVerified();
        return true;
    }

    private async Task<EmailAuthenticator<Guid>> GetOrCreateAsync(AppUser user, CancellationToken cancellationToken)
    {
        EmailAuthenticator<Guid>? existing = await _authenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken);
        if (existing is not null)
            return existing;

        var created = new EmailAuthenticator<Guid>(user.Id);
        await _authenticators.AddAsync(created, cancellationToken);
        return created;
    }
}
