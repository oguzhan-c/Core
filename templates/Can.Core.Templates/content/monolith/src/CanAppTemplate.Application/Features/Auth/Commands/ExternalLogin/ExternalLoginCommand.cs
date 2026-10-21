using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Sessions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth.Commands.ExternalLogin;

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

public sealed class ExternalLoginCommandHandler : IRequestHandler<ExternalLoginCommand, Result<LoginResult>>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly IRepository<Role<IdentityKey>, IdentityKey> _roles;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly EmailVerification _emailVerification;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;
    private readonly IUnitOfWork _unitOfWork;

    public ExternalLoginCommandHandler(
        IRepository<AppUser, IdentityKey> users,
        IRepository<Role<IdentityKey>, IdentityKey> roles,
        AuthTokenIssuer tokenIssuer,
        EmailVerification emailVerification,
        IUserSessionService sessions,
        ISecurityEvents events,
        TimeProvider timeProvider,
        IUnitOfWork unitOfWork)
    {
        _users = users;
        _roles = roles;
        _tokenIssuer = tokenIssuer;
        _emailVerification = emailVerification;
        _sessions = sessions;
        _events = events;
        _timeProvider = timeProvider;
        _unitOfWork = unitOfWork;
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
        if (IdentityKeys.IsDatabaseGenerated)
            await _unitOfWork.SaveChangesAsync(cancellationToken); // int / long anahtar: Id'ye bağlı kayıtlardan (doğrulama kodu, oturum) önce
        return user;
    }
}
