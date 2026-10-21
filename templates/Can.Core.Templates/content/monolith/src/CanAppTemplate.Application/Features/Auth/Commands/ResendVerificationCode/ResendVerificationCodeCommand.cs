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
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ResendVerificationCode;

/// <summary>Yeni doğrulama kodu gönderir. Hesabın var olup olmadığını belli etmemek için her zaman başarılı döner.</summary>
public sealed record ResendVerificationCodeCommand(string Email) : IRequest<Result<Success>>, ITransactionalRequest;

public sealed class ResendVerificationCodeCommandHandler : IRequestHandler<ResendVerificationCodeCommand, Result<Success>>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly EmailVerification _emailVerification;

    public ResendVerificationCodeCommandHandler(IRepository<AppUser, IdentityKey> users, EmailVerification emailVerification)
    {
        _users = users;
        _emailVerification = emailVerification;
    }

    public async Task<Result<Success>> Handle(ResendVerificationCodeCommand request, CancellationToken cancellationToken)
    {
        string normalizedEmail = request.Email.Trim().ToUpperInvariant();
        AppUser? user = await _users.GetAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken: cancellationToken);

        if (user is { EmailConfirmed: false })
            await _emailVerification.SendCodeAsync(user, VerificationPurpose.ConfirmEmail, cancellationToken);

        return Result.Success;
    }
}
