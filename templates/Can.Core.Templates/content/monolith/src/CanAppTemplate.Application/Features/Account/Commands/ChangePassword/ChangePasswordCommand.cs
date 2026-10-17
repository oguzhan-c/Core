using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Otp;
using Can.Core.Security.Passkeys;
using Can.Core.Security.RecoveryCodes;
using Fido2NetLib;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Application.Features.Account.Commands.ReissueCurrentSession;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.ChangePassword;

/// <summary>
/// Şifre değiştirir ya da (dış sağlayıcıyla açılmış, şifresiz hesapta) şifre belirler. Güvenlik damgası yenilenir:
/// diğer cihazlardaki oturumlar kapanır. Bu oturum WebApi'de yeni token'larla sürer (<see cref="ReissueCurrentSessionCommand"/>).
/// </summary>
public sealed record ChangePasswordCommand(string? CurrentPassword, string NewPassword) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class ChangePasswordCommandHandler : IRequestHandler<ChangePasswordCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly Can.Core.Security.Hashing.IPasswordHasher _passwordHasher;
    private readonly ISecurityEvents _events;

    public ChangePasswordCommandHandler(ICurrentUser currentUser, IRepository<AppUser, Guid> users, Can.Core.Security.Hashing.IPasswordHasher passwordHasher, ISecurityEvents events)
    {
        _currentUser = currentUser;
        _users = users;
        _passwordHasher = passwordHasher;
        _events = events;
    }

    public async Task<Result<Success>> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (user.PasswordHash is not null
            && (request.CurrentPassword is null || _passwordHasher.Verify(request.CurrentPassword, user.PasswordHash) == Can.Core.Security.Hashing.PasswordVerificationResult.Failed))
        {
            return Error.Validation("invalid_password", "Mevcut şifre hatalı.", "CurrentPassword");
        }

        user.SetPasswordHash(_passwordHasher.Hash(request.NewPassword));
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.PasswordChanged, user.Id.ToString()), cancellationToken);
        return Result.Success;
    }
}
