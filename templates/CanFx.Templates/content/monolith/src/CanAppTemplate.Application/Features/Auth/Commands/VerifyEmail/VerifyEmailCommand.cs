using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mailing;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Hashing;
using CanFx.Security.Sessions;
using CanFx.Security.VerificationCodes;
using FluentValidation;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.VerifyEmail;

/// <summary>E-postadaki kodu doğrular; başarılıysa kullanıcı doğrudan giriş yapmış olur.</summary>
public sealed record VerifyEmailCommand(string Email, string Code, ClientInfo Client) : IRequest<AuthResult>, ILoggableRequest;

public sealed class VerifyEmailCommandHandler : IRequestHandler<VerifyEmailCommand, AuthResult>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly EmailVerification _emailVerification;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AuthBusinessRules _authBusinessRules;

    public VerifyEmailCommandHandler(
        IRepository<AppUser, IdentityKey> users,
        EmailVerification emailVerification,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        AuthBusinessRules authBusinessRules)
    {
        _users = users;
        _emailVerification = emailVerification;
        _tokenIssuer = tokenIssuer;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _authBusinessRules = authBusinessRules;
    }

    public async Task<AuthResult> Handle(VerifyEmailCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);
        _authBusinessRules.VerificationCodeShouldBeValid(user is not null);

        // Doğrulanmış hesapta bu uç nokta şifresiz giriş kapısı olamaz (2FA atlanırdı): giriş sayfasına yönlendirilir.
        _authBusinessRules.EmailShouldNotBeConfirmedYet(user);

        bool valid = await _emailVerification.VerifyAsync(user, request.Code, cancellationToken);
        if (!valid)
            await _unitOfWork.SaveChangesAsync(cancellationToken); // başarısız deneme sayacı kalıcı olsun (kaba kuvvete karşı)

        _authBusinessRules.VerificationCodeShouldBeValid(valid);

        user.ConfirmEmail();

        AuthResult session = await AuthEvents.SignInAsync(user, request.Client, "email_verification", _tokenIssuer, _sessions, _events, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return session;
    }
}
