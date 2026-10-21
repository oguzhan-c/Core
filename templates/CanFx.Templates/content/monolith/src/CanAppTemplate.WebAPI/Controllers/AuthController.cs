using System.Text.Json;
using CanFx.Application.Exceptions;
using CanFx.Domain.Results;
using CanFx.Security.Passkeys;
using CanFx.WebApi;
using CanFx.WebApi.Caching;
using CanFx.WebApi.Controllers;
using CanFx.WebApi.DependencyInjection;
using CanFx.WebApi.RateLimiting;
using CanFx.WebApi.Security;
using Fido2NetLib;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Application.Features.Auth.Commands.ForgotPassword;
using CanAppTemplate.Application.Features.Auth.Commands.Login;
using CanAppTemplate.Application.Features.Auth.Commands.Logout;
#if (Passkey)
using CanAppTemplate.Application.Features.Auth.Commands.PasskeyLogin;
#endif
using CanAppTemplate.Application.Features.Auth.Commands.RefreshToken;
using CanAppTemplate.Application.Features.Auth.Commands.Register;
using CanAppTemplate.Application.Features.Auth.Commands.ResendVerificationCode;
using CanAppTemplate.Application.Features.Auth.Commands.ResetPassword;
using CanAppTemplate.Application.Features.Auth.Commands.VerifyEmail;
#if (TwoFactor)
using CanAppTemplate.Application.Features.Auth.Commands.CompleteTwoFactorLogin;
using CanAppTemplate.Application.Features.Auth.Commands.ResendTwoFactorCode;
#endif
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Features.Auth.Queries.GetProfile;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.WebAPI.Security;

namespace CanAppTemplate.WebAPI.Controllers;

/// <summary>
/// Giriş, kayıt, doğrulama, şifre sıfırlama ve çıkış (<c>/api/auth</c>). Token'lar yanıt gövdesinde DÖNMEZ: HttpOnly +
/// Secure + SameSite=Strict cookie olarak yazılır; tarayıcı sonraki isteklerde kendisi gönderir, JavaScript erişemez.
/// </summary>
[Route("api/auth")]
[ApiController]
public class AuthController : BaseController
{
    private readonly ClientInfoFactory _clients;
    private readonly IAuthCookieService _cookies;
    private readonly TwoFactorCookie _twoFactor;

    public AuthController(ClientInfoFactory clients, IAuthCookieService cookies, TwoFactorCookie twoFactor)
    {
        _clients = clients;
        _cookies = cookies;
        _twoFactor = twoFactor;
    }

    /// <param name="DeviceId">Mobil uygulamanın cihaz kimliği (tarayıcıda boş: sunucu cookie ile tanır).</param>
    /// <param name="DeviceName">Mobil uygulamanın cihaz adı ("Ayşe'nin iPhone'u").</param>
    public sealed record LoginRequest(string Email, string Password, string? DeviceId = null, string? DeviceName = null);

    public sealed record RegisterRequest(string Email, string Password, string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent);

    public sealed record VerifyEmailRequest(string Email, string Code);

    public sealed record EmailRequest(string Email);

    public sealed record ResetPasswordRequest(string Email, string Code, string NewPassword);

    /// <param name="IsRecoveryCode">Kod, authenticator yerine kurtarma kodlarından biri mi.</param>
    public sealed record TwoFactorRequest(string Code, bool IsRecoveryCode = false);

    /// <param name="Credential">Tarayıcının <c>navigator.credentials.get()</c> yanıtı (JSON).</param>
    public sealed record PasskeyLoginRequest(JsonElement Credential);

    /// <summary>Şifreli giriş yanıtı: ya kullanıcı (oturum açıldı) ya da ikinci adım bilgisi.</summary>
    public sealed record LoginResponse(UserProfileDto? User, TwoFactorPrompt? TwoFactor);

    public sealed record AuthFeatures(bool Passkeys);

    /// <summary>E-posta ve şifre ile giriş. İki adımlı doğrulama açıksa ikinci adım istenir; değilse token'lar cookie'ye yazılır.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Auth)]
    public async Task<IResult> Login([FromBody] LoginRequest body, CancellationToken ct)
    {
        LoggedResponse login = await Mediator.Send(new LoginCommand(body.Email, body.Password, _clients.Create(HttpContext, body.DeviceId, body.DeviceName)), ct);

        if (login.Challenge is { } challenge)
        {
            // Oturum henüz açılmadı: bekleyen giriş şifreli HttpOnly cookie'de, istemciye yalnızca yöntem bilgisi.
            _twoFactor.Write(Response, challenge);
            return TypedResults.Ok(new LoginResponse(null, login.Prompt));
        }

        _twoFactor.Clear(Response);
        _cookies.SetTokens(Response, login.Session!.AccessToken, login.Session.RefreshToken);
        return TypedResults.Ok(new LoginResponse(login.Session.User, null));
    }
#if (TwoFactor)

    /// <summary>İkinci adım: authenticator / e-posta kodu ya da kurtarma kodu.</summary>
    [HttpPost("login/two-factor")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Auth)]
    public async Task<IResult> CompleteTwoFactorLogin([FromBody] TwoFactorRequest body, CancellationToken ct)
    {
        if (_twoFactor.Read(Request) is not { } challenge)
            throw TwoFactorExpired();

        AuthResult session;
        try
        {
            session = await Mediator.Send(new CompleteTwoFactorLoginCommand(challenge, body.Code, body.IsRecoveryCode, _clients.Create(HttpContext)), ct);
        }
        catch (UnauthorizedException ex)
        {
            // Kilitlenme ya da geçersiz bekleyen giriş: baştan giriş yapılmalı, bekleyen giriş cookie'si silinir. Exception
            // handler yanıt başlıklarını (Set-Cookie dahil) temizlediği için hata yanıtı burada yazılır.
            _twoFactor.Clear(Response);
            return Error.Unauthorized(ex.Code ?? AuthBusinessMessages.UnauthorizedCode, ex.Message).ToProblem();
        }

        _twoFactor.Clear(Response);
        _cookies.SetTokens(Response, session.AccessToken, session.RefreshToken);
        return TypedResults.Ok(session.User);
    }

    /// <summary>İkinci adım e-posta ile yapılıyorsa yeni kod gönderir.</summary>
    [HttpPost("login/two-factor/resend")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> ResendTwoFactorCode(CancellationToken ct)
    {
        if (_twoFactor.Read(Request) is not { } challenge)
            throw TwoFactorExpired();

        await Mediator.Send(new ResendTwoFactorCodeCommand(challenge), ct);
        return TypedResults.NoContent();
    }

    private static UnauthorizedException TwoFactorExpired() =>
        new(AuthBusinessMessages.TwoFactorExpired) { Code = AuthBusinessMessages.TwoFactorExpiredCode };
#endif

    /// <summary>Sunucuda açık olan giriş yöntemleri.</summary>
    [HttpGet("features")]
    [AllowAnonymous]
    public IResult Features() => TypedResults.Ok(new AuthFeatures(HttpContext.RequestServices.GetService<IPasskeyService>() is not null));
#if (Passkey)

    /// <summary>Passkey ile girişin ilk adımı: tarayıcıya verilecek seçenekler (challenge).</summary>
    [HttpPost("passkey/options")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Auth)]
    public async Task<IResult> PasskeyOptions([FromServices] PasskeyCeremonyStore ceremonies, CancellationToken ct)
    {
        if (HttpContext.RequestServices.GetService<IPasskeyService>() is not { } passkeys)
            return TypedResults.NotFound();

        // Kullanıcı adı sorulmaz: cihaz bu site için kayıtlı passkey'lerden birini önerir.
        string json = passkeys.BeginLogin().ToJson();
        await ceremonies.SaveAsync(Response, PasskeyCeremonyStore.Login, json, ct);
        return Results.Content(json, "application/json");
    }

    /// <summary>Passkey ile girişin ikinci adımı: cihazın imzası doğrulanır, token'lar cookie'ye yazılır.</summary>
    [HttpPost("passkey/login")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Auth)]
    public async Task<IResult> PasskeyLogin([FromBody] PasskeyLoginRequest body, [FromServices] PasskeyCeremonyStore ceremonies, CancellationToken ct)
    {
        if (await ceremonies.TakeAsync(Request, Response, PasskeyCeremonyStore.Login, ct) is not { } optionsJson)
            throw new UnauthorizedException(AuthBusinessMessages.PasskeyExpired) { Code = AuthBusinessMessages.PasskeyFailedCode };

        AuthenticatorAssertionRawResponse credential = ReadCredential<AuthenticatorAssertionRawResponse>(body.Credential);
        AuthResult session = await Mediator.Send(new PasskeyLoginCommand(credential, AssertionOptions.FromJson(optionsJson), _clients.Create(HttpContext)), ct);
        _cookies.SetTokens(Response, session.AccessToken, session.RefreshToken);
        return TypedResults.Ok(session.User);
    }
#endif

    /// <summary>Üye ol; e-postaya 6 haneli doğrulama kodu gönderilir.</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Auth)]
    public async Task<IResult> Register([FromBody] RegisterRequest body, CancellationToken ct) =>
        TypedResults.Ok(await Mediator.Send(new RegisterCommand(body.Email, body.Password, body.FirstName, body.LastName, body.PhoneNumber, body.MarketingConsent), ct));

    /// <summary>E-posta doğrulama; başarılıysa kullanıcı giriş yapmış olur.</summary>
    [HttpPost("verify-email")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Auth)]
    public async Task<IResult> VerifyEmail([FromBody] VerifyEmailRequest body, CancellationToken ct)
    {
        AuthResult session = await Mediator.Send(new VerifyEmailCommand(body.Email, body.Code, _clients.Create(HttpContext)), ct);
        _cookies.SetTokens(Response, session.AccessToken, session.RefreshToken);
        return TypedResults.Ok(session.User);
    }

    /// <summary>Yeni doğrulama kodu gönderir.</summary>
    [HttpPost("resend-code")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> ResendCode([FromBody] EmailRequest body, CancellationToken ct)
    {
        await Mediator.Send(new ResendVerificationCodeCommand(body.Email), ct);
        return TypedResults.NoContent();
    }

    /// <summary>Şifre sıfırlama kodu gönderir (hesap yoksa da başarılı döner).</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Sensitive)]
    public async Task<IResult> ForgotPassword([FromBody] EmailRequest body, CancellationToken ct)
    {
        await Mediator.Send(new ForgotPasswordCommand(body.Email), ct);
        return TypedResults.NoContent();
    }

    /// <summary>Kodla yeni şifre; tüm oturumlar kapanır.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(CanRateLimitPolicies.Auth)]
    public async Task<IResult> ResetPassword([FromBody] ResetPasswordRequest body, CancellationToken ct)
    {
        await Mediator.Send(new ResetPasswordCommand(body.Email, body.Code, body.NewPassword), ct);
        return TypedResults.NoContent();
    }

    /// <summary>Refresh token cookie'si ile yeni token çifti (rotasyon; oturum aynı kalır).</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IResult> Refresh(CancellationToken ct)
    {
        if (_cookies.GetRefreshToken(Request) is not { } refreshToken)
            throw new UnauthorizedException(AuthBusinessMessages.SessionExpired) { Code = AuthBusinessMessages.SessionExpiredCode };

        AuthResult session;
        try
        {
            session = await Mediator.Send(new RefreshTokenCommand(refreshToken, _clients.Create(HttpContext)), ct);
        }
        catch (UnauthorizedException ex)
        {
            // Geçersiz/çalınmış token: tarayıcıdaki cookie'leri de temizle (yanıt burada yazılır, bkz. CompleteTwoFactorLogin).
            _cookies.Clear(Response);
            return Error.Unauthorized(ex.Code ?? AuthBusinessMessages.UnauthorizedCode, ex.Message).ToProblem();
        }

        _cookies.SetTokens(Response, session.AccessToken, session.RefreshToken);
        return TypedResults.Ok(session.User);
    }

    /// <summary>Bu oturumu kapatır ve cookie'leri siler.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IResult> Logout(CancellationToken ct)
    {
        await Mediator.Send(new LogoutCommand(_cookies.GetRefreshToken(Request), _clients.Create(HttpContext)), ct);
        await HttpContext.RevokeCurrentAccessTokenAsync(ct); // kopyalanmış access token da hemen 401 olsun
        _cookies.Clear(Response);
        return TypedResults.NoContent();
    }

    /// <summary>Giriş yapmış kullanıcının profili.</summary>
    [HttpGet("me")]
    [Authorize]
    [NoStoreResponse]
    public async Task<IResult> Me(CancellationToken ct) => TypedResults.Ok(await Mediator.Send(new GetProfileQuery(), ct));

    /// <summary>
    /// Tarayıcının WebAuthn yanıtını Fido2 tipine çevirir. Uygulamanın JSON ayarları yerine Fido2'nin kendi
    /// öznitelikleri geçerli olsun diye varsayılan ayarlarla okunur.
    /// </summary>
    internal static T ReadCredential<T>(JsonElement credential)
        where T : class
    {
        try
        {
            if (JsonSerializer.Deserialize<T>(credential.GetRawText()) is { } value)
                return value;
        }
        catch (JsonException)
        {
        }

        throw new CanFx.Application.Exceptions.ValidationException("credential", AuthBusinessMessages.PasskeyInvalidResponse)
        {
            Code = AuthBusinessMessages.PasskeyFailedCode,
        };
    }
}
