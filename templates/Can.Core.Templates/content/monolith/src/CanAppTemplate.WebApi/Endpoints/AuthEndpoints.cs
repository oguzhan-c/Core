using System.Text.Json;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Security.Passkeys;
using Can.Core.WebApi;
using Can.Core.WebApi.Caching;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.RateLimiting;
using Can.Core.WebApi.Security;
using Fido2NetLib;
using CanAppTemplate.Application.Features.Auth;
using CanAppTemplate.WebApi.Security;

namespace CanAppTemplate.WebApi.Endpoints;

/// <summary>
/// Giriş, kayıt, doğrulama, şifre sıfırlama ve çıkış. Token'lar yanıt gövdesinde DÖNMEZ: HttpOnly + Secure +
/// SameSite=Strict cookie olarak yazılır; tarayıcı sonraki isteklerde kendisi gönderir, JavaScript erişemez.
/// </summary>
internal static class AuthEndpoints
{
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

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest body, ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, TwoFactorCookie twoFactor, HttpContext http, CancellationToken ct) =>
            {
                Result<LoginResult> result = await sender.Send(new LoginCommand(body.Email, body.Password, clients.Create(http, body.DeviceId, body.DeviceName)), ct);

                return result.ToHttpResult(login =>
                {
                    if (login.Challenge is { } challenge)
                    {
                        // Oturum henüz açılmadı: bekleyen giriş şifreli HttpOnly cookie'de, istemciye yalnızca yöntem bilgisi.
                        twoFactor.Write(http.Response, challenge);
                        return TypedResults.Ok(new LoginResponse(null, login.Prompt));
                    }

                    twoFactor.Clear(http.Response);
                    cookies.SetTokens(http.Response, login.Session!.AccessToken, login.Session.RefreshToken);
                    return TypedResults.Ok(new LoginResponse(login.Session.User, null));
                });
            })
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("E-posta ve şifre ile giriş. İki adımlı doğrulama açıksa ikinci adım istenir; değilse token'lar cookie'ye yazılır.");

#if (TwoFactor)
        group.MapPost("/login/two-factor", async (TwoFactorRequest body, ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, TwoFactorCookie twoFactor, HttpContext http, CancellationToken ct) =>
            {
                if (twoFactor.Read(http.Request) is not { } challenge)
                    return AuthErrors.TwoFactorExpired.ToProblem();

                Result<AuthResult> result = await sender.Send(new CompleteTwoFactorLoginCommand(challenge, body.Code, body.IsRecoveryCode, clients.Create(http)), ct);

                // Kilitlenme ya da geçersiz bekleyen giriş: baştan giriş yapılmalı.
                if (result.IsFailure && result.FirstError.Type == ErrorType.Unauthorized)
                    twoFactor.Clear(http.Response);

                return result.ToHttpResult(session =>
                {
                    twoFactor.Clear(http.Response);
                    cookies.SetTokens(http.Response, session.AccessToken, session.RefreshToken);
                    return TypedResults.Ok(session.User);
                });
            })
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("İkinci adım: authenticator / e-posta kodu ya da kurtarma kodu.");

        group.MapPost("/login/two-factor/resend", async (ISender sender, TwoFactorCookie twoFactor, HttpContext http, CancellationToken ct) =>
                twoFactor.Read(http.Request) is { } challenge
                    ? await sender.Send(new ResendTwoFactorCodeCommand(challenge), ct).ToHttpResult()
                    : AuthErrors.TwoFactorExpired.ToProblem())
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Sensitive)
            .WithSummary("İkinci adım e-posta ile yapılıyorsa yeni kod gönderir.");
#endif

        bool passkeysEnabled = app.ServiceProvider.GetService<IPasskeyService>() is not null;

        group.MapGet("/features", () => TypedResults.Ok(new AuthFeatures(passkeysEnabled)))
            .AllowAnonymous()
            .WithResponseCache(ResponseCacheScope.Public, TimeSpan.FromMinutes(10))
            .WithSummary("Sunucuda açık olan giriş yöntemleri.");

        if (passkeysEnabled)
        {
            group.MapPost("/passkey/options", async (IPasskeyService passkeys, PasskeyCeremonyStore ceremonies, HttpContext http, CancellationToken ct) =>
                {
                    // Kullanıcı adı sorulmaz: cihaz bu site için kayıtlı passkey'lerden birini önerir.
                    string json = passkeys.BeginLogin().ToJson();
                    await ceremonies.SaveAsync(http.Response, PasskeyCeremonyStore.Login, json, ct);
                    return Results.Content(json, "application/json");
                })
                .AllowAnonymous()
                .RequireRateLimiting(CanRateLimitPolicies.Auth)
                .WithSummary("Passkey ile girişin ilk adımı: tarayıcıya verilecek seçenekler (challenge).");

            group.MapPost("/passkey/login", async (PasskeyLoginRequest body, ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, PasskeyCeremonyStore ceremonies, HttpContext http, CancellationToken ct) =>
                {
                    if (await ceremonies.TakeAsync(http.Request, http.Response, PasskeyCeremonyStore.Login, ct) is not { } optionsJson)
                        return PasskeyExpired.ToProblem();

                    Result<AuthenticatorAssertionRawResponse> credential = ReadCredential<AuthenticatorAssertionRawResponse>(body.Credential);
                    if (credential.IsFailure)
                        return credential.Errors.ToProblem();

                    Result<AuthResult> result = await sender.Send(new PasskeyLoginCommand(credential.Value, AssertionOptions.FromJson(optionsJson), clients.Create(http)), ct);
                    return result.ToHttpResult(session =>
                    {
                        cookies.SetTokens(http.Response, session.AccessToken, session.RefreshToken);
                        return TypedResults.Ok(session.User);
                    });
                })
                .AllowAnonymous()
                .RequireRateLimiting(CanRateLimitPolicies.Auth)
                .WithSummary("Passkey ile girişin ikinci adımı: cihazın imzası doğrulanır, token'lar cookie'ye yazılır.");
        }

        group.MapPost("/register", (RegisterRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new RegisterCommand(body.Email, body.Password, body.FirstName, body.LastName, body.PhoneNumber, body.MarketingConsent), ct).ToHttpResult())
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("Üye ol; e-postaya 6 haneli doğrulama kodu gönderilir.");

        group.MapPost("/verify-email", async (VerifyEmailRequest body, ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
                (await sender.Send(new VerifyEmailCommand(body.Email, body.Code, clients.Create(http)), ct)).ToHttpResult(session =>
                {
                    cookies.SetTokens(http.Response, session.AccessToken, session.RefreshToken);
                    return TypedResults.Ok(session.User);
                }))
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("E-posta doğrulama; başarılıysa kullanıcı giriş yapmış olur.");

        group.MapPost("/resend-code", (EmailRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new ResendVerificationCodeCommand(body.Email), ct).ToHttpResult())
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Sensitive)
            .WithSummary("Yeni doğrulama kodu gönderir.");

        group.MapPost("/forgot-password", (EmailRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new ForgotPasswordCommand(body.Email), ct).ToHttpResult())
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Sensitive)
            .WithSummary("Şifre sıfırlama kodu gönderir (hesap yoksa da başarılı döner).");

        group.MapPost("/reset-password", (ResetPasswordRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new ResetPasswordCommand(body.Email, body.Code, body.NewPassword), ct).ToHttpResult())
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("Kodla yeni şifre; tüm oturumlar kapanır.");

        group.MapPost("/refresh", async (ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                if (cookies.GetRefreshToken(http.Request) is not { } refreshToken)
                    return AuthErrors.SessionExpired.ToProblem();

                Result<AuthResult> result = await sender.Send(new RefreshTokenCommand(refreshToken, clients.Create(http)), ct);

                // Geçersiz/çalınmış token: tarayıcıdaki cookie'leri de temizle.
                if (result.IsFailure)
                    cookies.Clear(http.Response);

                return result.ToHttpResult(session =>
                {
                    cookies.SetTokens(http.Response, session.AccessToken, session.RefreshToken);
                    return TypedResults.Ok(session.User);
                });
            })
            .AllowAnonymous()
            .WithSummary("Refresh token cookie'si ile yeni token çifti (rotasyon; oturum aynı kalır).");

        group.MapPost("/logout", async (ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                await sender.Send(new LogoutCommand(cookies.GetRefreshToken(http.Request), clients.Create(http)), ct);
                await http.RevokeCurrentAccessTokenAsync(ct); // kopyalanmış access token da hemen 401 olsun
                cookies.Clear(http.Response);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .WithSummary("Bu oturumu kapatır ve cookie'leri siler.");

        group.MapGet("/me", (ISender sender, CancellationToken ct) => sender.Send(new GetProfileQuery(), ct).ToHttpResult())
            .RequireAuthorization()
            .WithNoStore()
            .WithSummary("Giriş yapmış kullanıcının profili.");
    }

    private static readonly Error PasskeyExpired = Error.Unauthorized(AuthErrorCodes.PasskeyFailed, "Passkey isteğinin süresi doldu; tekrar dene.");

    /// <summary>
    /// Tarayıcının WebAuthn yanıtını Fido2 tipine çevirir. Uygulamanın JSON ayarları yerine Fido2'nin kendi
    /// öznitelikleri geçerli olsun diye varsayılan ayarlarla okunur.
    /// </summary>
    internal static Result<T> ReadCredential<T>(JsonElement credential)
        where T : class
    {
        Error invalid = Error.Validation(AuthErrorCodes.PasskeyFailed, "Geçersiz passkey yanıtı.", "credential");

        try
        {
            return JsonSerializer.Deserialize<T>(credential.GetRawText()).ToResult(invalid);
        }
        catch (JsonException)
        {
            return invalid;
        }
    }
}
