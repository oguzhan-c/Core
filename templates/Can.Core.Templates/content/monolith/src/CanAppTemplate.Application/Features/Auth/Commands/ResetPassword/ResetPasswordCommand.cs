using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Events;
using Can.Core.Security.Hashing;
using Can.Core.Security.Sessions;
using Can.Core.Security.Tokens;
using FluentValidation;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ResetPassword;

/// <summary>
/// Kodla yeni şifre belirler. Güvenlik damgası yenilenir: tüm oturumlar (çalınmış olabilecekler dahil) kapanır;
/// kullanıcı yeni şifresiyle giriş yapar. Hesap kilitliyse kilit kalkar.
/// </summary>
public sealed record ResetPasswordCommand(string Email, string Code, string NewPassword) : IRequest, ILoggableRequest;

public sealed class ResetPasswordCommandHandler : IRequestHandler<ResetPasswordCommand>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly EmailVerification _emailVerification;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityStampValidator? _stampValidator;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AuthBusinessRules _authBusinessRules;

    public ResetPasswordCommandHandler(
        IRepository<AppUser, IdentityKey> users,
        EmailVerification emailVerification,
        IPasswordHasher passwordHasher,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        IEnumerable<ISecurityStampValidator> stampValidators,
        AuthBusinessRules authBusinessRules)
    {
        _users = users;
        _emailVerification = emailVerification;
        _passwordHasher = passwordHasher;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _stampValidator = stampValidators.FirstOrDefault();
        _authBusinessRules = authBusinessRules;
    }

    public async Task Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken: cancellationToken);
        _authBusinessRules.VerificationCodeShouldBeValid(user is not null);

        bool valid = await _emailVerification.VerifyAsync(user, request.Code, cancellationToken);
        if (!valid)
            await _unitOfWork.SaveChangesAsync(cancellationToken); // başarısız deneme sayacı kalıcı olsun (exception'dan ÖNCE)

        _authBusinessRules.VerificationCodeShouldBeValid(valid);

        user.SetPasswordHash(_passwordHasher.Hash(request.NewPassword)); // damgayı yeniler
        user.ResetAccessFailed();
        user.ConfirmEmail(); // kodu e-postadan aldı: adres doğrulanmış sayılır
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _sessions.RevokeAllAsync(user.Id.ToString(), cancellationToken);
        _stampValidator?.Invalidate(user.Id.ToString());
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasswordReset, user.Id.ToString()), cancellationToken);
    }
}
