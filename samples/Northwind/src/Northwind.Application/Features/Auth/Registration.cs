using System.Security.Cryptography;
using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mailing;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Hashing;
using Can.Core.Security.VerificationCodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Customers;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

// ---------------------------------------------------------------- kayıt

/// <summary>
/// Siteden müşteri kaydı: kullanıcı (Customer rolü) ve ona bağlı müşteri kaydı oluşturulur, e-postaya 6 haneli
/// doğrulama kodu gönderilir. E-posta doğrulanana kadar giriş yapılamaz.
/// </summary>
public sealed record RegisterCommand(
    string Tenant,
    string Email,
    string Password,
    string FirstName,
    string LastName,
    string CompanyName,
    string? Phone) : IRequest<Result<RegisterResult>>, ITransactionalRequest, ILoggableRequest;

public sealed record RegisterResult(string Email, DateTimeOffset CodeExpiresAt);

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(c => c.Tenant).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.Password)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128)
            .Matches("[A-Za-zÇĞİÖŞÜçğıöşü]").WithMessage("Şifre en az bir harf içermeli.")
            .Matches("[0-9]").WithMessage("Şifre en az bir rakam içermeli.");
        RuleFor(c => c.FirstName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.LastName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.CompanyName).NotEmpty().MaximumLength(Customer.CompanyNameMaxLength);
        RuleFor(c => c.Phone).MaximumLength(Customer.PhoneMaxLength);
    }
}

public sealed class RegisterCommandHandler : IRequestHandler<RegisterCommand, Result<RegisterResult>>
{
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<Role<Guid>, Guid> _roles;
    private readonly IRepository<Customer, Guid> _customers;
    private readonly IPasswordHasher _passwordHasher;
    private readonly EmailVerification _emailVerification;

    public RegisterCommandHandler(
        StoreTenant storeTenant,
        IRepository<AppUser, Guid> users,
        IRepository<Role<Guid>, Guid> roles,
        IRepository<Customer, Guid> customers,
        IPasswordHasher passwordHasher,
        EmailVerification emailVerification)
    {
        _storeTenant = storeTenant;
        _users = users;
        _roles = roles;
        _customers = customers;
        _passwordHasher = passwordHasher;
        _emailVerification = emailVerification;
    }

    public async Task<Result<RegisterResult>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        Result<Can.Core.MultiTenancy.TenantInfo> store = await _storeTenant.UseAsync(request.Tenant, cancellationToken);
        if (store.IsFailure)
            return store.Errors;

        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        if (await _users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, withDeleted: true, cancellationToken: cancellationToken))
            return AuthErrors.EmailTaken;

        // Seed eksikse bu bir kurulum hatasıdır (beklenmeyen): exception olarak kalır.
        Role<Guid> customerRole =
            await _roles.GetAsync(r => r.Name == Roles.Customer, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Customer rolü bulunamadı; seed çalıştı mı?");

        Result<AppUser> registered = AppUser.Register(request.Email, request.FirstName, request.LastName, _passwordHasher.Hash(request.Password));
        if (registered.IsFailure)
            return registered.Errors;

        AppUser user = registered.Value;
        user.AddRole(customerRole);
        await _users.AddAsync(user, cancellationToken);

        Result<string> code = await NewCustomerCodeAsync(request.CompanyName, cancellationToken);
        if (code.IsFailure)
            return code.Errors;

        Result<Customer> customer = Customer
            .Create(code.Value, request.CompanyName, $"{request.FirstName} {request.LastName}", null, null, request.Phone, null)
            .Then(c => c.LinkUser(user.Id).Map(_ => c));
        if (customer.IsFailure)
            return customer.Errors;

        await _customers.AddAsync(customer.Value, cancellationToken);

        DateTimeOffset expiresAt = await _emailVerification.SendCodeAsync(user, cancellationToken);
        return new RegisterResult(user.Email, expiresAt);
    }

    /// <summary>Firma adından 3 harf + 2 rakam (ör. "ACM07"); mağaza içinde benzersiz.</summary>
    private async Task<Result<string>> NewCustomerCodeAsync(string companyName, CancellationToken cancellationToken)
    {
        string letters = new string(companyName.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z').Take(3).ToArray()).PadRight(3, 'X');

        for (int attempt = 0; attempt < 20; attempt++)
        {
            string code = letters + RandomNumberGenerator.GetInt32(0, 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            if (!await _customers.AnyAsync(c => c.Code == code, withDeleted: true, cancellationToken: cancellationToken))
                return code;
        }

        return Error.Conflict("customer.code_exhausted", "Müşteri kodu üretilemedi; tekrar dene.");
    }
}

// ---------------------------------------------------------------- doğrulama

/// <summary>E-postadaki kodu doğrular; başarılıysa kullanıcı doğrudan giriş yapmış olur.</summary>
public sealed record VerifyEmailCommand(string Tenant, string Email, string Code, string? IpAddress = null) : IRequest<Result<AuthResult>>, ILoggableRequest;

public sealed class VerifyEmailCommandValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailCommandValidator()
    {
        RuleFor(c => c.Tenant).NotEmpty();
        RuleFor(c => c.Email).NotEmpty().EmailAddress();
        RuleFor(c => c.Code).NotEmpty().Length(6).Matches("^[0-9]{6}$").WithMessage("Kod 6 haneli olmalı.");
    }
}

public sealed class VerifyEmailCommandHandler : IRequestHandler<VerifyEmailCommand, Result<AuthResult>>
{
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly EmailVerification _emailVerification;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUnitOfWork _unitOfWork;

    public VerifyEmailCommandHandler(
        StoreTenant storeTenant,
        IRepository<AppUser, Guid> users,
        EmailVerification emailVerification,
        AuthTokenIssuer tokenIssuer,
        IUnitOfWork unitOfWork)
    {
        _storeTenant = storeTenant;
        _users = users;
        _emailVerification = emailVerification;
        _tokenIssuer = tokenIssuer;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<AuthResult>> Handle(VerifyEmailCommand request, CancellationToken cancellationToken)
    {
        if ((await _storeTenant.UseAsync(request.Tenant, cancellationToken)).IsFailure)
            return AuthErrors.InvalidVerificationCode;

        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(
            u => u.NormalizedEmail == normalizedEmail,
            include: q => q.Include(u => u.UserRoles).ThenInclude(ur => ur.Role),
            cancellationToken: cancellationToken
        );
        if (user is null)
            return AuthErrors.InvalidVerificationCode;

        if (!user.EmailConfirmed)
        {
            bool valid = await _emailVerification.VerifyAsync(user, request.Code, cancellationToken);

            if (!valid)
            {
                // Başarısız deneme sayacı kalıcı olsun (kaba kuvvet denemelerine karşı).
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return AuthErrors.InvalidVerificationCode;
            }

            user.ConfirmEmail();
        }

        AuthResult result = await _tokenIssuer.IssueAsync(user, request.IpAddress, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>Yeni doğrulama kodu gönderir. Hesabın var olup olmadığını belli etmemek için her zaman başarılı döner.</summary>
public sealed record ResendVerificationCodeCommand(string Tenant, string Email) : IRequest<Result<Success>>, ITransactionalRequest;

public sealed class ResendVerificationCodeCommandHandler : IRequestHandler<ResendVerificationCodeCommand, Result<Success>>
{
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly EmailVerification _emailVerification;

    public ResendVerificationCodeCommandHandler(StoreTenant storeTenant, IRepository<AppUser, Guid> users, EmailVerification emailVerification)
    {
        _storeTenant = storeTenant;
        _users = users;
        _emailVerification = emailVerification;
    }

    public async Task<Result<Success>> Handle(ResendVerificationCodeCommand request, CancellationToken cancellationToken)
    {
        // Hesabın var olup olmadığı belli olmasın: mağaza ya da kullanıcı yoksa da başarılı döner.
        if ((await _storeTenant.UseAsync(request.Tenant, cancellationToken)).IsFailure)
            return Result.Success;

        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken: cancellationToken);

        if (user is { EmailConfirmed: false })
            await _emailVerification.SendCodeAsync(user, cancellationToken);

        return Result.Success;
    }
}

// ---------------------------------------------------------------- servisler

/// <summary>
/// E-posta doğrulama kodları: kod HMAC'lenmiş olarak <see cref="EmailAuthenticator{TId}"/>'da saklanır,
/// 10 dakika geçerlidir ve en fazla 5 kez denenebilir.
/// </summary>
public sealed class EmailVerification
{
    private readonly IRepository<EmailAuthenticator<Guid>, Guid> _authenticators;
    private readonly IVerificationCodeService _codes;
    private readonly IEmailSender _emailSender;
    private readonly TenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;

    public EmailVerification(
        IRepository<EmailAuthenticator<Guid>, Guid> authenticators,
        IVerificationCodeService codes,
        IEmailSender emailSender,
        TenantContext tenantContext,
        TimeProvider timeProvider)
    {
        _authenticators = authenticators;
        _codes = codes;
        _emailSender = emailSender;
        _tenantContext = tenantContext;
        _timeProvider = timeProvider;
    }

    /// <summary>E-posta adresini doğrulama kodu.</summary>
    public Task<DateTimeOffset> SendCodeAsync(AppUser user, CancellationToken cancellationToken) =>
        SendCodeAsync(user, signIn: false, cancellationToken);

    /// <param name="signIn"><see langword="true"/>: iki adımlı girişin ikinci adımı için (e-posta metni buna göre).</param>
    public async Task<DateTimeOffset> SendCodeAsync(AppUser user, bool signIn, CancellationToken cancellationToken)
    {
        EmailAuthenticator<Guid> authenticator = await GetOrCreateAsync(user, cancellationToken);
        VerificationCode code = _codes.Generate();
        authenticator.SetCode(code);

        string store = _tenantContext.Tenant?.Name ?? "Northwind";
        string purpose = signIn ? $"{store} hesabına giriş yapmak için" : $"{store} hesabını doğrulamak için";

        var message = new EmailMessage(signIn ? $"Giriş kodun: {code.Code}" : $"Doğrulama kodun: {code.Code}")
        {
            TextBody =
                $"Merhaba {user.FirstName},\n\n{purpose} kodun: {code.Code}\n\n"
                + "Kod 10 dakika geçerlidir. Bu isteği sen yapmadıysan bu e-postayı yok sayabilirsin"
                + (signIn ? " ve şifreni değiştirmeni öneririz." : "."),
            HtmlBody =
                $"<p>Merhaba {System.Net.WebUtility.HtmlEncode(user.FirstName)},</p>"
                + $"<p>{System.Net.WebUtility.HtmlEncode(purpose)} kodun:</p><p style=\"font-size:28px;font-weight:bold;letter-spacing:6px\">{code.Code}</p>"
                + "<p>Kod 10 dakika geçerlidir.</p>",
        };
        message.To.Add(new EmailAddress(user.Email, $"{user.FirstName} {user.LastName}"));
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
