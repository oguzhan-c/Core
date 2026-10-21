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
using CanAppTemplate.Application.Features.Account.Rules;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.RegenerateRecoveryCodes;

/// <summary>Kurtarma kodlarını yeniler (eskileri geçersiz olur). Yalnızca iki adımlı doğrulama açıkken.</summary>
public sealed record RegenerateRecoveryCodesCommand(string? Password) : IRequest<RecoveryCodesDto>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class RegenerateRecoveryCodesCommandHandler : IRequestHandler<RegenerateRecoveryCodesCommand, RecoveryCodesDto>
{
    private readonly Can.Core.Security.Hashing.IPasswordHasher _passwordHasher;
    private readonly RecoveryCodeIssuer _recoveryCodes;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public RegenerateRecoveryCodesCommandHandler(
        Can.Core.Security.Hashing.IPasswordHasher passwordHasher,
        RecoveryCodeIssuer recoveryCodes,
        AuthBusinessRules authBusinessRules,
        AccountBusinessRules accountBusinessRules)
    {
        _passwordHasher = passwordHasher;
        _recoveryCodes = recoveryCodes;
        _authBusinessRules = authBusinessRules;
        _accountBusinessRules = accountBusinessRules;
    }

    public async Task<RecoveryCodesDto> Handle(RegenerateRecoveryCodesCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(cancellationToken: cancellationToken);
        _accountBusinessRules.TwoFactorShouldBeEnabled(user);
        _authBusinessRules.PasswordShouldMatch(
            user.PasswordHash is null
            || (request.Password is not null
                && _passwordHasher.Verify(request.Password, user.PasswordHash) != Can.Core.Security.Hashing.PasswordVerificationResult.Failed));

        return await _recoveryCodes.RegenerateAsync(user.Id, cancellationToken);
    }
}
