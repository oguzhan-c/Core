using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Events;
using Can.Core.Security.Hashing;
using Can.Core.Security.Sessions;
using Can.Core.Security.Tokens;
using FluentValidation;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ForgotPassword;

/// <summary>
/// "Şifremi unuttum": e-postaya kod gönderilir. Hesabın var olup olmadığı belli olmasın diye her zaman başarılı döner.
/// </summary>
public sealed record ForgotPasswordCommand(string Email) : IRequest<Result<Success>>, ITransactionalRequest, ILoggableRequest;

public sealed class ForgotPasswordCommandHandler : IRequestHandler<ForgotPasswordCommand, Result<Success>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly EmailVerification _emailVerification;
    private readonly ISecurityEvents _events;

    public ForgotPasswordCommandHandler(IRepository<AppUser, Guid> users, EmailVerification emailVerification, ISecurityEvents events)
    {
        _users = users;
        _emailVerification = emailVerification;
        _events = events;
    }

    public async Task<Result<Success>> Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken: cancellationToken);
        if (user is null)
            return Result.Success;

        await _emailVerification.SendCodeAsync(user, VerificationPurpose.ResetPassword, cancellationToken);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasswordResetRequested, user.Id.ToString()), cancellationToken);
        return Result.Success;
    }
}
