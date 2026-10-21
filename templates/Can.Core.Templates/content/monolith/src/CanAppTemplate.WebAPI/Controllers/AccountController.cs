using System.Text.Json;
using Can.Core.Domain.Results;
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
using CanAppTemplate.Application.Features.Auth.Constants;
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
    public async Task<IResult> UpdateProfile([FromBody] ProfileRequest body, CancellationToken ct) =>
        (await Mediator.Send(new UpdateProfileCommand(body.FirstName, body.LastName, body.PhoneNumber, body.MarketingConsent), ct)).ToHttpResult();

    /// <summary>Şifre, iki adımlı doğrulama, kurtarma kodları, passkey'ler ve bağlı dış hesaplar.</summary>
    [HttpGet("security")]
    public async Task<IResult> Security(CancellationToken ct) => (await Mediator.Send(new GetAccountSecurityQuery(), ct)).ToHttpResult();

    /// <summary>Şifre değiştirir (şifresiz hesapta şifre belirler). Diğer cihazlardaki oturumlar kapanır.</summary>
    [HttpPost("password")]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> ChangePassword([FromBody] ChangePasswordRequest body, CancellationToken ct) =>
        await ThenReissueAsync(await Mediator.Send(new ChangePasswordCommand(body.CurrentPassword, body.NewPassword), ct), ct);
#if (Otp)

    /// <summary>Authenticator uygulaması kurulumu: QR kodu adresi ve elle girilecek anahtar.</summary>
    [HttpPost("two-factor/otp/setup")]
    public async Task<IResult> BeginOtpSetup(CancellationToken ct) => (await Mediator.Send(new BeginOtpSetupCommand(), ct)).ToHttpResult();

    /// <summary>Uygulamadaki ilk kod ile kurulumu tamamlar; kurtarma kodları bir kez döner.</summary>
    [HttpPost("two-factor/otp/enable")]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> EnableOtp([FromBody] CodeRequest body, CancellationToken ct) =>
        await ThenReissueAsync(await Mediator.Send(new EnableOtpCommand(body.Code), ct), ct);
#endif
#if (EmailTwoFactor)

    /// <summary>Girişte e-postaya gönderilen kod istenir; kurtarma kodları bir kez döner.</summary>
    [HttpPost("two-factor/email/enable")]
    public async Task<IResult> EnableEmailTwoFactor(CancellationToken ct) =>
        await ThenReissueAsync(await Mediator.Send(new EnableEmailTwoFactorCommand(), ct), ct);
#endif
#if (TwoFactor)

    /// <summary>İki adımlı doğrulamayı kapatır (şifre gerekir).</summary>
    [HttpPost("two-factor/disable")]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> DisableTwoFactor([FromBody] PasswordRequest body, CancellationToken ct) =>
        await ThenReissueAsync(await Mediator.Send(new DisableTwoFactorCommand(body.Password), ct), ct);

    /// <summary>Kurtarma kodlarını yeniler; eskiler geçersiz olur.</summary>
    [HttpPost("two-factor/recovery-codes")]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> RegenerateRecoveryCodes([FromBody] OptionalPasswordRequest body, CancellationToken ct) =>
        (await Mediator.Send(new RegenerateRecoveryCodesCommand(body.Password), ct)).ToHttpResult();
#endif
#if (ExternalLogin)

    /// <summary>Bağlı dış hesabı kaldırır; hesaba girmenin tek yolu buysa kaldırılmaz. Bağlamak için: GET /api/auth/external/{provider}/link</summary>
    [HttpDelete("external-logins/{provider}")]
    public async Task<IResult> UnlinkExternalLogin(string provider, CancellationToken ct) =>
        await ThenReissueAsync(await Mediator.Send(new UnlinkExternalLoginCommand(provider), ct), ct);
#endif
#if (Passkey)

    /// <summary>Passkey eklemenin ilk adımı: tarayıcıya verilecek seçenekler.</summary>
    [HttpPost("passkeys/options")]
    public async Task<IResult> BeginPasskeyRegistration([FromServices] PasskeyCeremonyStore ceremonies, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService<IPasskeyService>() is null)
            return TypedResults.NotFound();

        Result<CredentialCreateOptions> options = await Mediator.Send(new BeginPasskeyRegistrationCommand(), ct);
        if (options.IsFailure)
            return options.Errors.ToProblem();

        string json = options.Value.ToJson();
        await ceremonies.SaveAsync(Response, PasskeyCeremonyStore.Register, json, ct);
        return Results.Content(json, "application/json");
    }

    /// <summary>Passkey eklemenin ikinci adımı: cihazın yanıtı doğrulanır ve passkey kaydedilir.</summary>
    [HttpPost("passkeys")]
    public async Task<IResult> CompletePasskeyRegistration([FromBody] AddPasskeyRequest body, [FromServices] PasskeyCeremonyStore ceremonies, CancellationToken ct)
    {
        if (await ceremonies.TakeAsync(Request, Response, PasskeyCeremonyStore.Register, ct) is not { } optionsJson)
            return Error.Failure(AuthErrorCodes.PasskeyFailed, "Passkey isteğinin süresi doldu; tekrar dene.").ToProblem();

        Result<AuthenticatorAttestationRawResponse> credential = AuthController.ReadCredential<AuthenticatorAttestationRawResponse>(body.Credential);
        if (credential.IsFailure)
            return credential.Errors.ToProblem();

        return (await Mediator.Send(new CompletePasskeyRegistrationCommand(credential.Value, CredentialCreateOptions.FromJson(optionsJson), body.Name), ct)).ToHttpResult();
    }

    /// <summary>Passkey'in adını değiştirir.</summary>
    [HttpPut("passkeys/{id}")]
    public async Task<IResult> RenamePasskey(IdentityKey id, [FromBody] NameRequest body, CancellationToken ct) =>
        (await Mediator.Send(new RenamePasskeyCommand(id, body.Name), ct)).ToHttpResult();

    /// <summary>Passkey'i siler; hesaba girmenin tek yolu buysa silinmez.</summary>
    [HttpDelete("passkeys/{id}")]
    public async Task<IResult> DeletePasskey(IdentityKey id, CancellationToken ct) =>
        (await Mediator.Send(new DeletePasskeyCommand(id), ct)).ToHttpResult();
#endif

    /// <summary>
    /// Damgayı değiştiren işlem başarılıysa bu oturuma yeni token'lar yazılır (diğer cihazlar kapanır). Yanıt gövdesi
    /// işlemin sonucudur (ör. kurtarma kodları).
    /// </summary>
    private async Task<IResult> ThenReissueAsync<T>(Result<T> result, CancellationToken ct)
    {
        if (result.IsFailure)
            return result.ToHttpResult();

        Result<AuthResult> session = await Mediator.Send(new ReissueCurrentSessionCommand(_cookies.GetRefreshToken(Request), _clients.Create(HttpContext)), ct);
        if (session.IsSuccess)
            _cookies.SetTokens(Response, session.Value.AccessToken, session.Value.RefreshToken);
        else
            _cookies.Clear(Response); // bu oturum da kapandı: istemci giriş sayfasına gider

        return result.ToHttpResult();
    }
}
