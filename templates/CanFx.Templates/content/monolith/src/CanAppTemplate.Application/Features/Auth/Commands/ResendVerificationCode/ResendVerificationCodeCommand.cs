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
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ResendVerificationCode;

/// <summary>Yeni doğrulama kodu gönderir. Hesabın var olup olmadığını belli etmemek için her zaman başarılı döner.</summary>
public sealed record ResendVerificationCodeCommand(string Email) : IRequest, ITransactionalRequest;

public sealed class ResendVerificationCodeCommandHandler : IRequestHandler<ResendVerificationCodeCommand>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly EmailVerification _emailVerification;

    public ResendVerificationCodeCommandHandler(IRepository<AppUser, IdentityKey> users, EmailVerification emailVerification)
    {
        _users = users;
        _emailVerification = emailVerification;
    }

    public async Task Handle(ResendVerificationCodeCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken: cancellationToken);

        if (user is { EmailConfirmed: false })
            await _emailVerification.SendCodeAsync(user, VerificationPurpose.ConfirmEmail, cancellationToken);
    }
}
