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
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.RegenerateRecoveryCodes;

/// <summary>Kurtarma kodlarını yeniler (eskileri geçersiz olur). Yalnızca iki adımlı doğrulama açıkken.</summary>
public sealed record RegenerateRecoveryCodesCommand(string? Password) : IRequest<Result<RecoveryCodesDto>>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class RegenerateRecoveryCodesCommandHandler : IRequestHandler<RegenerateRecoveryCodesCommand, Result<RecoveryCodesDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly Can.Core.Security.Hashing.IPasswordHasher _passwordHasher;
    private readonly RecoveryCodeIssuer _recoveryCodes;

    public RegenerateRecoveryCodesCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        Can.Core.Security.Hashing.IPasswordHasher passwordHasher,
        RecoveryCodeIssuer recoveryCodes)
    {
        _currentUser = currentUser;
        _users = users;
        _passwordHasher = passwordHasher;
        _recoveryCodes = recoveryCodes;
    }

    public async Task<Result<RecoveryCodesDto>> Handle(RegenerateRecoveryCodesCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        if (user.AuthenticatorType == AuthenticatorType.None)
            return Error.Failure("two_factor_disabled", "Kurtarma kodları yalnızca iki adımlı doğrulama açıkken kullanılır.");

        if (user.PasswordHash is not null
            && (request.Password is null || _passwordHasher.Verify(request.Password, user.PasswordHash) == Can.Core.Security.Hashing.PasswordVerificationResult.Failed))
        {
            return AuthErrors.InvalidPassword;
        }

        return await _recoveryCodes.RegenerateAsync(user.Id, cancellationToken);
    }
}
