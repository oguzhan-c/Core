using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Events;
using CanFx.Security.Hashing;
using CanFx.Security.Sessions;
using CanFx.Security.Tokens;
using FluentValidation;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ForgotPassword;

/// <summary>
/// "Şifremi unuttum": e-postaya kod gönderilir. Hesabın var olup olmadığı belli olmasın diye her zaman başarılı döner.
/// </summary>
public sealed record ForgotPasswordCommand(string Email) : IRequest, ITransactionalRequest, ILoggableRequest;

public sealed class ForgotPasswordCommandHandler : IRequestHandler<ForgotPasswordCommand>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly EmailVerification _emailVerification;
    private readonly ISecurityEvents _events;

    public ForgotPasswordCommandHandler(IRepository<AppUser, IdentityKey> users, EmailVerification emailVerification, ISecurityEvents events)
    {
        _users = users;
        _emailVerification = emailVerification;
        _events = events;
    }

    public async Task Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken: cancellationToken);
        if (user is null)
            return;

        await _emailVerification.SendCodeAsync(user, VerificationPurpose.ResetPassword, cancellationToken);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasswordResetRequested, user.Id.ToString()), cancellationToken);
    }
}
