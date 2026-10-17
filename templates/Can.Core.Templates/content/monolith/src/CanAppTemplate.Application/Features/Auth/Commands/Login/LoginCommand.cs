using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Hashing;
using Can.Core.Security.Sessions;
using FluentValidation;
using CanAppTemplate.Application.Features.Auth.Commands.CompleteTwoFactorLogin;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.Login;

/// <summary>
/// E-posta + şifre ile giriş. İki adımlı doğrulama açıksa oturum açılmaz, ikinci adım istenir
/// (<see cref="CompleteTwoFactorLoginCommand"/>). Başarılı/başarısız girişler, kilitlenme ve yeni cihazdan giriş
/// güvenlik olayı olarak yayınlanır.
/// </summary>
public sealed record LoginCommand(string Email, string Password, ClientInfo Client) : IRequest<Result<LoginResult>>, ILoggableRequest;

public sealed class LoginCommandHandler : IRequestHandler<LoginCommand, Result<LoginResult>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly EmailVerification _emailVerification;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public LoginCommandHandler(
        IRepository<AppUser, Guid> users,
        IPasswordHasher passwordHasher,
        AuthTokenIssuer tokenIssuer,
        EmailVerification emailVerification,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _users = users;
        _passwordHasher = passwordHasher;
        _tokenIssuer = tokenIssuer;
        _emailVerification = emailVerification;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<LoginResult>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
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
            await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.LoginFailed, user?.Id.ToString()) { Reason = user is null ? "unknown_user" : "no_password" }, cancellationToken);
            return AuthErrors.InvalidCredentials;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (user.IsLockedOut(now))
        {
            await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.LoginFailed, user.Id.ToString()) { Reason = "locked_out" }, cancellationToken);
            return AuthErrors.LockedOut;
        }

        PasswordVerificationResult verification = _passwordHasher.Verify(request.Password, user.PasswordHash);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.RegisterFailedAccess(now);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await AuthEvents.FailedAsync(_events, user, now, "invalid_password", cancellationToken);
            return user.IsLockedOut(now) ? AuthErrors.LockedOut : AuthErrors.InvalidCredentials;
        }

        // İterasyon artırıldıysa hash yükseltilir; damga değişmez (kullanıcının diğer oturumları kapanmaz).
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.UpgradePasswordHash(_passwordHasher.Hash(request.Password));

        user.ResetAccessFailed();

        // Şifre doğrulandıktan SONRA: aksi hâlde e-postanın kayıtlı olup olmadığı anlaşılırdı.
        if (!user.EmailConfirmed)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return AuthErrors.EmailNotConfirmed;
        }

        if (user.AuthenticatorType != AuthenticatorType.None)
        {
            string? destination = null;
            if (user.AuthenticatorType == AuthenticatorType.Email)
            {
                await _emailVerification.SendCodeAsync(user, VerificationPurpose.SignIn, cancellationToken);
                destination = EmailMask.Mask(user.Email);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return LoginResult.RequiresTwoFactor(
                new TwoFactorChallenge(user.Id, user.SecurityStamp, user.AuthenticatorType),
                new TwoFactorPrompt(user.AuthenticatorType, destination)
            );
        }

        AuthResult result = await AuthEvents.SignInAsync(user, request.Client, "password", _tokenIssuer, _sessions, _events, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return LoginResult.SignedIn(result);
    }
}
