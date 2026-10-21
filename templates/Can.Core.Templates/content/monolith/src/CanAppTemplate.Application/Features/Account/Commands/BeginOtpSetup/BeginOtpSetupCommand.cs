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
using CanAppTemplate.Application.Features.Account.Commands.EnableOtp;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.BeginOtpSetup;

/// <summary>
/// Authenticator uygulaması kurulumunun ilk adımı: yeni gizli anahtar üretilir (henüz aktif değil). Kullanıcı QR kodu
/// okutup uygulamadaki ilk kodu <see cref="EnableOtpCommand"/> ile girince iki adımlı giriş açılır.
/// </summary>
public sealed record BeginOtpSetupCommand : IRequest<OtpSetupDto>, ISecuredRequest, ITransactionalRequest, ILoggableRequest;

public sealed class BeginOtpSetupCommandHandler : IRequestHandler<BeginOtpSetupCommand, OtpSetupDto>
{
    private readonly IRepository<OtpAuthenticator<IdentityKey>, IdentityKey> _otpAuthenticators;
    private readonly ITotpService _totp;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AuthBusinessRules _authBusinessRules;
    private readonly AccountBusinessRules _accountBusinessRules;

    public BeginOtpSetupCommandHandler(
        IRepository<OtpAuthenticator<IdentityKey>, IdentityKey> otpAuthenticators,
        ITotpService totp,
        IUnitOfWork unitOfWork,
        AuthBusinessRules authBusinessRules,
        AccountBusinessRules accountBusinessRules)
    {
        _otpAuthenticators = otpAuthenticators;
        _totp = totp;
        _unitOfWork = unitOfWork;
        _authBusinessRules = authBusinessRules;
        _accountBusinessRules = accountBusinessRules;
    }

    public async Task<OtpSetupDto> Handle(BeginOtpSetupCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(cancellationToken: cancellationToken);
        _accountBusinessRules.OtpShouldNotBeEnabled(user);

        // Yarım kalmış eski kurulum varsa silinir (kullanıcı başına tek kayıt; benzersiz index).
        if (await _otpAuthenticators.GetAsync(a => a.UserId == user.Id, cancellationToken: cancellationToken) is { } existing)
        {
            _otpAuthenticators.Delete(existing, permanent: true);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        byte[] secret = _totp.GenerateSecret();
        await _otpAuthenticators.AddAsync(new OtpAuthenticator<IdentityKey>(user.Id, secret), cancellationToken);

        string base32 = Base32.Encode(secret);
        return new OtpSetupDto(
            string.Join(' ', base32.Chunk(4).Select(c => new string(c))), // elle girerken okunur olsun: ABCD EFGH ...
            _totp.GetProvisioningUri(secret, AppInfo.Name, user.Email)
        );
    }
}
