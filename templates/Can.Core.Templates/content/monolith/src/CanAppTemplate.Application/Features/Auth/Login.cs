using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Hashing;
using Can.Core.Security.Sessions;
using FluentValidation;
using CanAppTemplate.Application.Common;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth;

/// <summary>
/// E-posta + şifre ile giriş. İki adımlı doğrulama açıksa oturum açılmaz, ikinci adım istenir
/// (<see cref="CompleteTwoFactorLoginCommand"/>). Başarılı/başarısız girişler, kilitlenme ve yeni cihazdan giriş
/// güvenlik olayı olarak yayınlanır.
/// </summary>
public sealed record LoginCommand(string Email, string Password, ClientInfo Client) : IRequest<Result<LoginResult>>, ILoggableRequest;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(128);
    }
}

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

/// <summary>Girişlerin ortak adımları ve güvenlik olayları.</summary>
internal static class AuthEvents
{
    /// <summary>
    /// Oturumu açar; cihaz daha önce görülmediyse "yeni cihazdan giriş" olayı yayınlanır (e-posta bildirimi bu olaya
    /// bağlıdır). Cihaz kimliği gönderilmeyen istemcilerde (tarayıcı ilk kez) olay yayınlanmaz.
    /// </summary>
    public static async Task<AuthResult> SignInAsync(
        AppUser user,
        ClientInfo client,
        string method,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        CancellationToken cancellationToken)
    {
        bool newDevice = client.DeviceId is { Length: > 0 } deviceId
            && !await sessions.IsKnownDeviceAsync(user.Id.ToString(), deviceId, cancellationToken);

        AuthResult result = await tokenIssuer.IssueAsync(user, client, cancellationToken);

        var properties = new Dictionary<string, string?> { ["method"] = method };
        await events.PublishAsync(
            new SecurityEvent(SecurityEventTypes.LoginSucceeded, user.Id.ToString()) { SessionId = result.SessionId, DeviceId = client.DeviceId, Properties = properties },
            cancellationToken
        );

        if (newDevice)
        {
            await events.PublishAsync(
                new SecurityEvent(SecurityEventTypes.LoginFromNewDevice, user.Id.ToString())
                {
                    SessionId = result.SessionId,
                    DeviceId = client.DeviceId,
                    Properties = new Dictionary<string, string?> { ["deviceName"] = client.DeviceName, ["method"] = method },
                },
                cancellationToken
            );
        }

        return result;
    }

    /// <summary>Başarısız deneme; hesap bu denemeyle kilitlendiyse ayrıca kilitlenme olayı.</summary>
    public static async Task FailedAsync(ISecurityEvents events, AppUser user, DateTimeOffset now, string reason, CancellationToken cancellationToken, string type = SecurityEventTypes.LoginFailed)
    {
        await events.PublishAsync(new SecurityEvent(type, user.Id.ToString()) { Reason = reason }, cancellationToken);

        if (user.IsLockedOut(now))
            await events.PublishAsync(new SecurityEvent(SecurityEventTypes.AccountLocked, user.Id.ToString()) { Reason = reason }, cancellationToken);
    }
}
