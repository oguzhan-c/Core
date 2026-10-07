using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Customers;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

// ---------------------------------------------------------------- dış sağlayıcıyla giriş

/// <summary>
/// Dış sağlayıcıyla (Google, Microsoft, GitHub) giriş. Kullanıcı şu sırayla bulunur:
/// <list type="number">
/// <item>Bu sağlayıcı hesabı bu mağazada bir kullanıcıya bağlıysa o kullanıcı.</item>
/// <item>Sağlayıcı e-postayı DOĞRULANMIŞ verdiyse aynı e-postalı kullanıcı (hesap otomatik bağlanır).</item>
/// <item>Hiçbiri yoksa ve e-posta doğrulanmışsa yeni müşteri hesabı (şifresiz).</item>
/// </list>
/// Doğrulanmamış e-postayla mevcut hesaba bağlanmaz: aksi hâlde başkasının e-postasını sağlayıcıya yazan biri o
/// hesabı ele geçirebilirdi. İki adımlı doğrulama açıksa şifreli girişteki gibi ikinci adım istenir.
/// </summary>
public sealed record ExternalLoginCommand(
    string Tenant,
    string Provider,
    string ProviderDisplayName,
    string ProviderKey,
    string? Email,
    bool EmailVerified,
    string? FirstName,
    string? LastName,
    string? FullName,
    string? IpAddress = null) : IRequest<Result<LoginResult>>, ITransactionalRequest, ILoggableRequest;

public sealed class ExternalLoginCommandValidator : AbstractValidator<ExternalLoginCommand>
{
    public ExternalLoginCommandValidator()
    {
        RuleFor(c => c.Tenant).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Provider).NotEmpty().MaximumLength(UserLogin<Guid>.ProviderMaxLength);
        RuleFor(c => c.ProviderKey).NotEmpty().MaximumLength(UserLogin<Guid>.ProviderKeyMaxLength);
        RuleFor(c => c.Email).MaximumLength(256);
    }
}

public sealed class ExternalLoginCommandHandler : IRequestHandler<ExternalLoginCommand, Result<LoginResult>>
{
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<Role<Guid>, Guid> _roles;
    private readonly IRepository<Customer, Guid> _customers;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly EmailVerification _emailVerification;
    private readonly TimeProvider _timeProvider;

    public ExternalLoginCommandHandler(
        StoreTenant storeTenant,
        IRepository<AppUser, Guid> users,
        IRepository<Role<Guid>, Guid> roles,
        IRepository<Customer, Guid> customers,
        AuthTokenIssuer tokenIssuer,
        EmailVerification emailVerification,
        TimeProvider timeProvider)
    {
        _storeTenant = storeTenant;
        _users = users;
        _roles = roles;
        _customers = customers;
        _tokenIssuer = tokenIssuer;
        _emailVerification = emailVerification;
        _timeProvider = timeProvider;
    }

    public async Task<Result<LoginResult>> Handle(ExternalLoginCommand request, CancellationToken cancellationToken)
    {
        Result<TenantInfo> store = await _storeTenant.UseAsync(request.Tenant, cancellationToken);
        if (store.IsFailure)
            return store.Errors;

        string provider = request.Provider.Trim().ToLowerInvariant();
        string key = request.ProviderKey.Trim();
        DateTimeOffset now = _timeProvider.GetUtcNow();

        AppUser? user = await _users.GetAsync(
            u => u.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == key),
            include: q => q.WithRolesAndPermissions().Include(u => u.Logins),
            cancellationToken: cancellationToken
        );

        if (user is null)
        {
            if (!request.EmailVerified || string.IsNullOrWhiteSpace(request.Email))
                return AuthErrors.ExternalEmailNotVerified;

            string normalizedEmail = request.Email.Trim().ToUpperInvariant();
            user = await _users.GetAsync(
                u => u.NormalizedEmail == normalizedEmail,
                include: q => q.WithRolesAndPermissions().Include(u => u.Logins),
                cancellationToken: cancellationToken
            );

            if (user is null)
            {
                // Silinmiş bir hesabın e-postası: yeni hesap açılamaz (benzersiz e-posta).
                if (await _users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, withDeleted: true, cancellationToken: cancellationToken))
                    return AuthErrors.EmailTaken;

                Result<AppUser> created = await CreateCustomerAsync(request, cancellationToken);
                if (created.IsFailure)
                    return created.Errors;

                user = created.Value;
            }

            // Aynı sağlayıcıda başka bir hesap zaten bağlıysa otomatik bağlama yapılmaz.
            if (!user.AddLogin(provider, key, request.ProviderDisplayName, now))
                return AuthErrors.ExternalProviderLinked;
        }

        if (user.IsLockedOut(now))
            return AuthErrors.LockedOut;

        // Sağlayıcı e-postayı doğruladıysa hesabın e-postası da doğrulanmış sayılır (kod beklemeye gerek yok).
        if (!user.EmailConfirmed)
        {
            if (!request.EmailVerified || !string.Equals(user.NormalizedEmail, request.Email?.Trim().ToUpperInvariant(), StringComparison.Ordinal))
                return AuthErrors.EmailNotConfirmed;

            user.ConfirmEmail();
        }

        if (user.AuthenticatorType != AuthenticatorType.None)
        {
            string? destination = null;
            if (user.AuthenticatorType == AuthenticatorType.Email)
            {
                await _emailVerification.SendCodeAsync(user, signIn: true, cancellationToken);
                destination = EmailMask.Mask(user.Email);
            }

            return LoginResult.RequiresTwoFactor(
                new TwoFactorChallenge(store.Value.Identifier, user.Id, user.SecurityStamp, user.AuthenticatorType),
                new TwoFactorPrompt(user.AuthenticatorType, destination)
            );
        }

        AuthResult session = await _tokenIssuer.IssueAsync(user, request.IpAddress, cancellationToken);
        return LoginResult.SignedIn(session);
    }

    /// <summary>İlk girişte müşteri hesabı: Customer rolü + kullanıcıya bağlı müşteri kaydı (firma adı = ad soyad).</summary>
    private async Task<Result<AppUser>> CreateCustomerAsync(ExternalLoginCommand request, CancellationToken cancellationToken)
    {
        Role<Guid> customerRole =
            await _roles.GetAsync(r => r.Name == Roles.Customer, include: q => q.Include(r => r.OperationClaims).ThenInclude(rc => rc.OperationClaim), cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Customer rolü bulunamadı; seed çalıştı mı?");

        (string firstName, string lastName) = ExternalNames.Split(request.FirstName, request.LastName, request.FullName, request.Email!);

        Result<AppUser> registered = AppUser.RegisterExternal(request.Email!, firstName, lastName);
        if (registered.IsFailure)
            return registered.Errors;

        AppUser user = registered.Value;
        user.AddRole(customerRole);
        await _users.AddAsync(user, cancellationToken);

        string fullName = $"{firstName} {lastName}";
        Result<Customer> customer = await CustomerCodes
            .NewAsync(_customers, fullName, cancellationToken)
            .Then(code => Customer.Create(code, fullName, fullName, null, null, null, null))
            .Then(c => c.LinkUser(user.Id).Map(_ => c));
        if (customer.IsFailure)
            return customer.Errors;

        await _customers.AddAsync(customer.Value, cancellationToken);
        return user;
    }
}

internal static class ExternalNames
{
    /// <summary>Sağlayıcının verdiği adlardan ad/soyad; yoksa tam addan ya da e-postanın kullanıcı adı kısmından.</summary>
    public static (string FirstName, string LastName) Split(string? givenName, string? familyName, string? fullName, string email)
    {
        string first = givenName?.Trim() ?? string.Empty;
        string last = familyName?.Trim() ?? string.Empty;

        if (first.Length == 0 && !string.IsNullOrWhiteSpace(fullName))
        {
            string[] parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            first = parts.Length > 1 ? string.Join(' ', parts[..^1]) : parts[0];
            if (last.Length == 0 && parts.Length > 1)
                last = parts[^1];
        }

        if (first.Length == 0)
            first = email[..Math.Max(1, email.IndexOf('@', StringComparison.Ordinal))];

        if (last.Length == 0)
            last = "-";

        return (Truncate(first), Truncate(last));
    }

    private static string Truncate(string value) => value.Length <= AppUser.NameMaxLength ? value : value[..AppUser.NameMaxLength];
}

// ---------------------------------------------------------------- hesaba bağlama

/// <summary>
/// Giriş yapmış kullanıcının hesabına dış hesap bağlar. Kullanıcı ve mağaza, sağlayıcıya giden şifreli state'ten
/// gelir (dönüşte oturum cookie'si gönderilmez); istemciden alınmaz.
/// </summary>
public sealed record LinkExternalLoginCommand(string Tenant, Guid UserId, string Provider, string ProviderDisplayName, string ProviderKey)
    : IRequest<Result<Success>>, ITransactionalRequest, ILoggableRequest;

public sealed class LinkExternalLoginCommandHandler : IRequestHandler<LinkExternalLoginCommand, Result<Success>>
{
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly TimeProvider _timeProvider;

    public LinkExternalLoginCommandHandler(StoreTenant storeTenant, IRepository<AppUser, Guid> users, TimeProvider timeProvider)
    {
        _storeTenant = storeTenant;
        _users = users;
        _timeProvider = timeProvider;
    }

    public async Task<Result<Success>> Handle(LinkExternalLoginCommand request, CancellationToken cancellationToken)
    {
        if ((await _storeTenant.UseAsync(request.Tenant, cancellationToken)).IsFailure)
            return Error.Unauthorized();

        AppUser? user = await _users.GetAsync(u => u.Id == request.UserId, include: q => q.Include(u => u.Logins), cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        string provider = request.Provider.Trim().ToLowerInvariant();
        string key = request.ProviderKey.Trim();

        if (user.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == key))
            return Result.Success; // zaten bağlı

        // UserLogins'in kendi tenant filtresi yok: kontrol mağazanın kullanıcıları üzerinden yapılır.
        if (await _users.AnyAsync(u => u.Id != user.Id && u.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == key), cancellationToken: cancellationToken))
            return AuthErrors.ExternalAlreadyLinked;

        if (!user.AddLogin(provider, key, request.ProviderDisplayName, _timeProvider.GetUtcNow()))
            return AuthErrors.ExternalProviderLinked;

        return Result.Success;
    }
}
