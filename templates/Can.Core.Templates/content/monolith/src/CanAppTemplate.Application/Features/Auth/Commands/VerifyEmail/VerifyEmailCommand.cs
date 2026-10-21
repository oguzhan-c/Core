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
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.VerifyEmail;

/// <summary>E-postadaki kodu doğrular; başarılıysa kullanıcı doğrudan giriş yapmış olur.</summary>
public sealed record VerifyEmailCommand(string Email, string Code, ClientInfo Client) : IRequest<Result<AuthResult>>, ILoggableRequest;

public sealed class VerifyEmailCommandHandler : IRequestHandler<VerifyEmailCommand, Result<AuthResult>>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly EmailVerification _emailVerification;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;

    public VerifyEmailCommandHandler(
        IRepository<AppUser, IdentityKey> users,
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
