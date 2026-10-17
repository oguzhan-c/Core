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

namespace CanAppTemplate.Application.Features.Auth.Commands.Register;

/// <summary>
/// Üye ol: müşteri hesabı açılır ve e-postaya 6 haneli doğrulama kodu gönderilir. E-posta doğrulanana kadar giriş
/// yapılamaz.
/// </summary>
public sealed record RegisterCommand(string Email, string Password, string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent)
    : IRequest<Result<RegisterResult>>, ITransactionalRequest, ILoggableRequest;

public sealed class RegisterCommandHandler : IRequestHandler<RegisterCommand, Result<RegisterResult>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<Role<Guid>, Guid> _roles;
    private readonly IPasswordHasher _passwordHasher;
    private readonly EmailVerification _emailVerification;

    public RegisterCommandHandler(IRepository<AppUser, Guid> users, IRepository<Role<Guid>, Guid> roles, IPasswordHasher passwordHasher, EmailVerification emailVerification)
    {
        _users = users;
        _roles = roles;
        _passwordHasher = passwordHasher;
        _emailVerification = emailVerification;
    }

    public async Task<Result<RegisterResult>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        if (await _users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, withDeleted: true, cancellationToken: cancellationToken))
            return AuthErrors.EmailTaken;

        Result<AppUser> registered = AppUser.Register(
            request.Email,
            request.FirstName,
            request.LastName,
            _passwordHasher.Hash(request.Password),
            request.MarketingConsent
        );
        if (registered.IsFailure)
            return registered.Errors;

        AppUser user = registered.Value;
        user.UpdateProfile(user.FirstName, user.LastName, request.PhoneNumber, request.MarketingConsent);
        user.AddRole(await DefaultRoles.GetAsync(_roles, cancellationToken));
        await _users.AddAsync(user, cancellationToken);

        DateTimeOffset expiresAt = await _emailVerification.SendCodeAsync(user, VerificationPurpose.ConfirmEmail, cancellationToken);
        return new RegisterResult(user.Email, expiresAt);
    }
}
