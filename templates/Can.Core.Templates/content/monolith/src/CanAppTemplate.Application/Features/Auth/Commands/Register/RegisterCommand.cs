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
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth.Commands.Register;

/// <summary>
/// Üye ol: müşteri hesabı açılır ve e-postaya 6 haneli doğrulama kodu gönderilir. E-posta doğrulanana kadar giriş
/// yapılamaz.
/// </summary>
public sealed record RegisterCommand(string Email, string Password, string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent)
    : IRequest<RegisteredResponse>, ITransactionalRequest, ILoggableRequest;

public sealed class RegisterCommandHandler : IRequestHandler<RegisterCommand, RegisteredResponse>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly IRepository<Role<IdentityKey>, IdentityKey> _roles;
    private readonly IPasswordHasher _passwordHasher;
    private readonly EmailVerification _emailVerification;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AuthBusinessRules _authBusinessRules;

    public RegisterCommandHandler(
        IRepository<AppUser, IdentityKey> users,
        IRepository<Role<IdentityKey>, IdentityKey> roles,
        IPasswordHasher passwordHasher,
        EmailVerification emailVerification,
        IUnitOfWork unitOfWork,
        AuthBusinessRules authBusinessRules)
    {
        _users = users;
        _roles = roles;
        _passwordHasher = passwordHasher;
        _emailVerification = emailVerification;
        _unitOfWork = unitOfWork;
        _authBusinessRules = authBusinessRules;
    }

    public async Task<RegisteredResponse> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        await _authBusinessRules.EmailShouldNotBeTaken(normalizedEmail, cancellationToken);

        AppUser user = AppUser.Register(
            request.Email,
            request.FirstName,
            request.LastName,
            _passwordHasher.Hash(request.Password),
            request.MarketingConsent
        ).OrThrow();

        user.UpdateProfile(user.FirstName, user.LastName, request.PhoneNumber, request.MarketingConsent).OrThrow();
        user.AddRole(await DefaultRoles.GetAsync(_roles, cancellationToken));
        await _users.AddAsync(user, cancellationToken);
        if (IdentityKeys.IsDatabaseGenerated)
            await _unitOfWork.SaveChangesAsync(cancellationToken); // int / long anahtar: Id'ye bağlı kayıtlardan (doğrulama kodu, oturum) önce

        DateTimeOffset expiresAt = await _emailVerification.SendCodeAsync(user, VerificationPurpose.ConfirmEmail, cancellationToken);
        return new RegisteredResponse(user.Email, expiresAt);
    }
}
