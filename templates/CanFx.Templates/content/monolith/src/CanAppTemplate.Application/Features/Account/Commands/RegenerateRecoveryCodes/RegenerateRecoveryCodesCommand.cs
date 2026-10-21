using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Otp;
using CanFx.Security.Passkeys;
using CanFx.Security.RecoveryCodes;
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
    private readonly CanFx.Security.Hashing.IPasswordHasher _passwordHasher;
    private readonly RecoveryCodeIssuer _recoveryCodes;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public RegenerateRecoveryCodesCommandHandler(
        CanFx.Security.Hashing.IPasswordHasher passwordHasher,
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
                && _passwordHasher.Verify(request.Password, user.PasswordHash) != CanFx.Security.Hashing.PasswordVerificationResult.Failed));

        return await _recoveryCodes.RegenerateAsync(user.Id, cancellationToken);
    }
}
