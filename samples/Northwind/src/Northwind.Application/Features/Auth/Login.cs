using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Hashing;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

/// <summary>
/// Mağaza + e-posta + şifre ile giriş. Tenant istemciden yalnızca BURADA alınır; sonraki tüm isteklerde imzalı
/// token'daki tenant kullanılır. Kullanıcı iki adımlı doğrulamayı açtıysa oturum açılmaz, ikinci adım istenir
/// (<see cref="CompleteTwoFactorLoginCommand"/>).
/// </summary>
public sealed record LoginCommand(string Tenant, string Email, string Password, string? IpAddress = null) : IRequest<Result<LoginResult>>, ILoggableRequest;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(c => c.Tenant).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class LoginCommandHandler : IRequestHandler<LoginCommand, Result<LoginResult>>
{
    private readonly ITenantStore _tenantStore;
    private readonly TenantContext _tenantContext;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly EmailVerification _emailVerification;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public LoginCommandHandler(
        ITenantStore tenantStore,
        TenantContext tenantContext,
        IRepository<AppUser, Guid> users,
        IPasswordHasher passwordHasher,
        AuthTokenIssuer tokenIssuer,
        EmailVerification emailVerification,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _emailVerification = emailVerification;
        _tenantStore = tenantStore;
        _tenantContext = tenantContext;
        _users = users;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<LoginResult>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        TenantInfo? tenant = await _tenantStore.FindAsync(request.Tenant.Trim(), cancellationToken);
        if (tenant is not { IsActive: true })
            return AuthErrors.InvalidCredentials;

        // Bu istekteki sorgular ve kayıtlar artık bu mağaza adına çalışır.
        _tenantContext.Set(tenant);

        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(
            u => u.NormalizedEmail == normalizedEmail,
            include: q => q.WithRolesAndPermissions(),
            cancellationToken: cancellationToken
        );

        if (user?.PasswordHash is null)
        {
            // Kullanıcı yokken de aynı süreyi harca: yanıt süresinden e-postanın kayıtlı olup olmadığı anlaşılmasın.
            _ = _passwordHasher.Hash(request.Password);
            return AuthErrors.InvalidCredentials;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (user.IsLockedOut(now))
            return AuthErrors.LockedOut;

        PasswordVerificationResult verification = _passwordHasher.Verify(request.Password, user.PasswordHash);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.RegisterFailedAccess(now);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return AuthErrors.InvalidCredentials;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.SetPasswordHash(_passwordHasher.Hash(request.Password));

        user.ResetAccessFailed();

        // Şifre doğrulandıktan SONRA: aksi hâlde e-postanın kayıtlı olup olmadığı anlaşılırdı.
        if (!user.EmailConfirmed)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return AuthErrors.EmailNotConfirmed;
        }

        // İki adımlı doğrulama açıksa oturum henüz açılmaz.
        if (user.AuthenticatorType != AuthenticatorType.None)
        {
            string? destination = null;
            if (user.AuthenticatorType == AuthenticatorType.Email)
            {
                await _emailVerification.SendCodeAsync(user, signIn: true, cancellationToken);
                destination = EmailMask.Mask(user.Email);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return LoginResult.RequiresTwoFactor(
                new TwoFactorChallenge(tenant.Identifier, user.Id, user.SecurityStamp, user.AuthenticatorType),
                new TwoFactorPrompt(user.AuthenticatorType, destination)
            );
        }

        AuthResult result = await _tokenIssuer.IssueAsync(user, request.IpAddress, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return LoginResult.SignedIn(result);
    }
}
