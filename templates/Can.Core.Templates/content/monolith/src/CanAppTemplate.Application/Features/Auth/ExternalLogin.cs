using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Sessions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Common;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth;

// ---------------------------------------------------------------- dış sağlayıcıyla giriş

/// <summary>
/// Dış sağlayıcıyla (Google, Microsoft, GitHub) giriş. Kullanıcı şu sırayla bulunur:
/// <list type="number">
/// <item>Bu sağlayıcı hesabı bir kullanıcıya bağlıysa o kullanıcı.</item>
/// <item>Sağlayıcı e-postayı DOĞRULANMIŞ verdiyse aynı e-postalı kullanıcı (hesap otomatik bağlanır).</item>
/// <item>Hiçbiri yoksa ve e-posta doğrulanmışsa yeni müşteri hesabı (şifresiz).</item>
/// </list>
/// Doğrulanmamış e-postayla mevcut hesaba bağlanmaz: aksi hâlde başkasının e-postasını sağlayıcıya yazan biri o
/// hesabı ele geçirebilirdi. İki adımlı doğrulama açıksa şifreli girişteki gibi ikinci adım istenir.
/// </summary>
public sealed record ExternalLoginCommand(
    string Provider,
    string ProviderDisplayName,
    string ProviderKey,
    string? Email,
    bool EmailVerified,
    string? FirstName,
    string? LastName,
    string? FullName,
    ClientInfo Client) : IRequest<Result<LoginResult>>, ITransactionalRequest, ILoggableRequest;

public sealed class ExternalLoginCommandValidator : AbstractValidator<ExternalLoginCommand>
{
    public ExternalLoginCommandValidator()
    {
        RuleFor(c => c.Provider).NotEmpty().MaximumLength(UserLogin<Guid>.ProviderMaxLength);
        RuleFor(c => c.ProviderKey).NotEmpty().MaximumLength(UserLogin<Guid>.ProviderKeyMaxLength);
        RuleFor(c => c.Email).MaximumLength(256);
    }
}

public sealed class ExternalLoginCommandHandler : IRequestHandler<ExternalLoginCommand, Result<LoginResult>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<Role<Guid>, Guid> _roles;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly EmailVerification _emailVerification;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;

    public ExternalLoginCommandHandler(
        IRepository<AppUser, Guid> users,
        IRepository<Role<Guid>, Guid> roles,
        AuthTokenIssuer tokenIssuer,
        EmailVerification emailVerification,
        IUserSessionService sessions,
        ISecurityEvents events,
        TimeProvider timeProvider)
    {
        _users = users;
        _roles = roles;
        _tokenIssuer = tokenIssuer;
        _emailVerification = emailVerification;
        _sessions = sessions;
        _events = events;
        _timeProvider = timeProvider;
    }

    public async Task<Result<LoginResult>> Handle(ExternalLoginCommand request, CancellationToken cancellationToken)
    {
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

            await _events.PublishAsync(
                new SecurityEvent(SecurityEventTypes.ExternalLoginLinked, user.Id.ToString()) { Properties = new Dictionary<string, string?> { ["provider"] = provider } },
                cancellationToken
            );
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
                await _emailVerification.SendCodeAsync(user, VerificationPurpose.SignIn, cancellationToken);
                destination = EmailMask.Mask(user.Email);
            }

            return LoginResult.RequiresTwoFactor(
                new TwoFactorChallenge(user.Id, user.SecurityStamp, user.AuthenticatorType),
                new TwoFactorPrompt(user.AuthenticatorType, destination)
            );
        }

        AuthResult session = await AuthEvents.SignInAsync(user, request.Client, provider, _tokenIssuer, _sessions, _events, cancellationToken);
        return LoginResult.SignedIn(session);
    }

    private async Task<Result<AppUser>> CreateCustomerAsync(ExternalLoginCommand request, CancellationToken cancellationToken)
    {
        (string firstName, string lastName) = ExternalNames.Split(request.FirstName, request.LastName, request.FullName, request.Email!);

        Result<AppUser> registered = AppUser.RegisterExternal(request.Email!, firstName, lastName);
        if (registered.IsFailure)
            return registered.Errors;

        AppUser user = registered.Value;
        user.AddRole(await DefaultRoles.GetAsync(_roles, cancellationToken));
        await _users.AddAsync(user, cancellationToken);
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
/// Giriş yapmış kullanıcının hesabına dış hesap bağlar. Kullanıcı, sağlayıcıya giden şifreli state'ten gelir (dönüşte
/// oturum cookie'si gönderilmez); istemciden alınmaz.
/// </summary>
public sealed record LinkExternalLoginCommand(Guid UserId, string Provider, string ProviderDisplayName, string ProviderKey)
    : IRequest<Result<Success>>, ITransactionalRequest, ILoggableRequest;

public sealed class LinkExternalLoginCommandHandler : IRequestHandler<LinkExternalLoginCommand, Result<Success>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;

    public LinkExternalLoginCommandHandler(IRepository<AppUser, Guid> users, ISecurityEvents events, TimeProvider timeProvider)
    {
        _users = users;
        _events = events;
        _timeProvider = timeProvider;
    }

    public async Task<Result<Success>> Handle(LinkExternalLoginCommand request, CancellationToken cancellationToken)
    {
        AppUser? user = await _users.GetAsync(u => u.Id == request.UserId, include: q => q.Include(u => u.Logins), cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        string provider = request.Provider.Trim().ToLowerInvariant();
        string key = request.ProviderKey.Trim();

        if (user.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == key))
            return Result.Success; // zaten bağlı

        if (await _users.AnyAsync(u => u.Id != user.Id && u.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == key), cancellationToken: cancellationToken))
            return AuthErrors.ExternalAlreadyLinked;

        if (!user.AddLogin(provider, key, request.ProviderDisplayName, _timeProvider.GetUtcNow()))
            return AuthErrors.ExternalProviderLinked;

        await _events.PublishAsync(
            new SecurityEvent(SecurityEventTypes.ExternalLoginLinked, user.Id.ToString()) { Properties = new Dictionary<string, string?> { ["provider"] = provider } },
            cancellationToken
        );
        return Result.Success;
    }
}
