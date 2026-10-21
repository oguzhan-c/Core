using System.Text.Json;
using Can.Core.Application.Exceptions;
using Can.Core.Security.Passkeys;
using Can.Core.WebApi;
using Can.Core.WebApi.Caching;
using Can.Core.WebApi.Controllers;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.RateLimiting;
using Fido2NetLib;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Application.Features.Account.Commands.ChangePassword;
using CanAppTemplate.Application.Features.Account.Commands.ReissueCurrentSession;
using CanAppTemplate.Application.Features.Account.Commands.UpdateProfile;
using CanAppTemplate.Application.Features.Account.Queries.GetAccountSecurity;
#if (Otp)
using CanAppTemplate.Application.Features.Account.Commands.BeginOtpSetup;
using CanAppTemplate.Application.Features.Account.Commands.EnableOtp;
#endif
#if (EmailTwoFactor)
using CanAppTemplate.Application.Features.Account.Commands.EnableEmailTwoFactor;
#endif
#if (TwoFactor)
using CanAppTemplate.Application.Features.Account.Commands.DisableTwoFactor;
using CanAppTemplate.Application.Features.Account.Commands.RegenerateRecoveryCodes;
#endif
#if (ExternalLogin)
using CanAppTemplate.Application.Features.Account.Commands.UnlinkExternalLogin;
#endif
#if (Passkey)
using CanAppTemplate.Application.Features.Account.Commands.BeginPasskeyRegistration;
using CanAppTemplate.Application.Features.Account.Commands.CompletePasskeyRegistration;
using CanAppTemplate.Application.Features.Account.Commands.DeletePasskey;
using CanAppTemplate.Application.Features.Account.Commands.RenamePasskey;
#endif
using CanAppTemplate.Application.Features.Account.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.WebAPI.Security;

namespace CanAppTemplate.WebAPI.Controllers;

/// <summary>
/// Giriş yapmış kullanıcının hesabı (<c>/api/auth/account</c>): profil, şifre, iki adımlı doğrulama, kurtarma kodları,
/// passkey'ler ve dış hesaplar. Güvenlik damgasını değiştiren işlemlerden sonra AYNI istekte bu oturuma yeni token verilir;
/// bunun için refresh token cookie'si (yolu <c>/api/auth</c>) bu adreslere de gönderilir. Oturum ve cihaz listesi core'un
/// <c>/api/sessions</c> uçlarındadır.
/// </summary>
[Route("api/auth/account")]
[ApiController]
[Authorize]
[NoStoreResponse]
public class AccountController : BaseController
{
    private readonly ClientInfoFactory _clients;
    private readonly IAuthCookieService _cookies;

    public AccountController(ClientInfoFactory clients, IAuthCookieService cookies)
    {
        _clients = clients;
        _cookies = cookies;
    }

    public sealed record CodeRequest(string Code);

    public sealed record PasswordRequest(string Password);

    public sealed record OptionalPasswordRequest(string? Password);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string NewPassword);

    public sealed record NameRequest(string Name);

    public sealed record ProfileRequest(string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent);

    /// <param name="Credential">Tarayıcının <c>navigator.credentials.create()</c> yanıtı (JSON).</param>
    public sealed record AddPasskeyRequest(string? Name, JsonElement Credential);

    /// <summary>Ad, soyad, telefon ve kampanya izni.</summary>
    [HttpPut("profile")]
    public async Task<IResult> UpdateProfile([FromBody] ProfileRequest body, CancellationToken ct)
    {
        await Mediator.Send(new UpdateProfileCommand(body.FirstName, body.LastName, body.PhoneNumber, body.MarketingConsent), ct);
        return TypedResults.NoContent();
    }

    [HttpGet("security")]
    public async Task<IResult> Security(CancellationToken ct) => TypedResults.Ok(await Mediator.Send(new GetAccountSecurityQuery(), ct));

    [HttpPost("password")]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> ChangePassword([FromBody] ChangePasswordRequest body, CancellationToken ct)
    {
        await Mediator.Send(new ChangePasswordCommand(body.CurrentPassword, body.NewPassword), ct);
        return await ReissueAsync(TypedResults.NoContent(), ct);
    }
#if (Otp)

    [HttpPost("two-factor/otp/setup")]
    public async Task<IResult> BeginOtpSetup(CancellationToken ct) => TypedResults.Ok(await Mediator.Send(new BeginOtpSetupCommand(), ct));

    [HttpPost("two-factor/otp/enable")]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> EnableOtp([FromBody] CodeRequest body, CancellationToken ct) =>
        await ReissueAsync(TypedResults.Ok(await Mediator.Send(new EnableOtpCommand(body.Code), ct)), ct);
#endif
#if (EmailTwoFactor)

    [HttpPost("two-factor/email/enable")]
    public async Task<IResult> EnableEmailTwoFactor(CancellationToken ct) =>
        await ReissueAsync(TypedResults.Ok(await Mediator.Send(new EnableEmailTwoFactorCommand(), ct)), ct);
#endif
#if (TwoFactor)

    [HttpPost("two-factor/disable")]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> DisableTwoFactor([FromBody] PasswordRequest body, CancellationToken ct)
    {
        await Mediator.Send(new DisableTwoFactorCommand(body.Password), ct);
        return await ReissueAsync(TypedResults.NoContent(), ct);
    }

    [HttpPost("two-factor/recovery-codes")]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> RegenerateRecoveryCodes([FromBody] OptionalPasswordRequest body, CancellationToken ct) =>
        TypedResults.Ok(await Mediator.Send(new RegenerateRecoveryCodesCommand(body.Password), ct));
#endif
#if (ExternalLogin)

    [HttpDelete("external-logins/{provider}")]
    public async Task<IResult> UnlinkExternalLogin(string provider, CancellationToken ct)
    {
        await Mediator.Send(new UnlinkExternalLoginCommand(provider), ct);
        return await ReissueAsync(TypedResults.NoContent(), ct);
    }
#endif
#if (Passkey)

    [HttpPost("passkeys/options")]
    public async Task<IResult> BeginPasskeyRegistration([FromServices] PasskeyCeremonyStore ceremonies, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService<IPasskeyService>() is null)
            return TypedResults.NotFound();

        CredentialCreateOptions options = await Mediator.Send(new BeginPasskeyRegistrationCommand(), ct);
        string json = options.ToJson();
        await ceremonies.SaveAsync(Response, PasskeyCeremonyStore.Register, json, ct);
        return Results.Content(json, "application/json");
    }

    [HttpPost("passkeys")]
    public async Task<IResult> CompletePasskeyRegistration([FromBody] AddPasskeyRequest body, [FromServices] PasskeyCeremonyStore ceremonies, CancellationToken ct)
    {
        if (await ceremonies.TakeAsync(Request, Response, PasskeyCeremonyStore.Register, ct) is not { } optionsJson)
            throw new Can.Core.Domain.Exceptions.BusinessException(AccountBusinessMessages.PasskeyRegistrationExpired) { Code = AccountBusinessMessages.PasskeyFailedCode };

        AuthenticatorAttestationRawResponse credential = AuthController.ReadCredential<AuthenticatorAttestationRawResponse>(body.Credential);
        return TypedResults.Ok(await Mediator.Send(new CompletePasskeyRegistrationCommand(credential, CredentialCreateOptions.FromJson(optionsJson), body.Name), ct));
    }

    [HttpPut("passkeys/{id}")]
    public async Task<IResult> RenamePasskey(IdentityKey id, [FromBody] NameRequest body, CancellationToken ct)
    {
        await Mediator.Send(new RenamePasskeyCommand(id, body.Name), ct);
        return TypedResults.NoContent();
    }

    [HttpDelete("passkeys/{id}")]
    public async Task<IResult> DeletePasskey(IdentityKey id, CancellationToken ct)
    {
        await Mediator.Send(new DeletePasskeyCommand(id), ct);
        return TypedResults.NoContent();
    }
#endif

    /// <summary>
    /// Güvenlik damgasını değiştiren işlemden (şifre, 2FA, dış hesap) SONRA bu oturuma yeni token verir; diğer oturumlar
    /// kapanır. Yenileme olmazsa (oturum da kapanmış) cookie'ler silinir ve istemci giriş sayfasına gider; işlemin kendisi
    /// başarılı olduğu için yanıtı yine de döner.
    /// </summary>
    private async Task<IResult> ReissueAsync(IResult success, CancellationToken ct)
    {
        try
        {
            AuthResult session = await Mediator.Send(new ReissueCurrentSessionCommand(_cookies.GetRefreshToken(Request), _clients.Create(HttpContext)), ct);
            _cookies.SetTokens(Response, session.AccessToken, session.RefreshToken);
        }
        catch (UnauthorizedException)
        {
            _cookies.Clear(Response);
        }

        return success;
    }
}
