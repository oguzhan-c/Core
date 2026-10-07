using System.Text.Json;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Security.Passkeys;
using Can.Core.WebApi;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.RateLimiting;
using Fido2NetLib;
using Northwind.Application.Features.Auth;
using Northwind.WebApi.Security;

namespace Northwind.WebApi.Endpoints;

/// <summary>
/// Giriş/çıkış. Token'lar yanıt gövdesinde DÖNMEZ; HttpOnly + Secure + SameSite=Strict cookie olarak yazılır.
/// Tarayıcı sonraki isteklerde cookie'yi kendisi gönderir, JavaScript token'a erişemez.
/// </summary>
internal static class AuthEndpoints
{
    public sealed record LoginRequest(string Tenant, string Email, string Password);

    public sealed record RegisterRequest(string Tenant, string Email, string Password, string FirstName, string LastName, string CompanyName, string? Phone);

    public sealed record VerifyEmailRequest(string Tenant, string Email, string Code);

    public sealed record ResendCodeRequest(string Tenant, string Email);

    public sealed record TwoFactorRequest(string Code);

    /// <param name="Credential">Tarayıcının <c>navigator.credentials.get()</c> yanıtı (JSON).</param>
    public sealed record PasskeyLoginRequest(string Tenant, JsonElement Credential);

    /// <summary>Şifreli giriş yanıtı: ya kullanıcı (oturum açıldı) ya da ikinci adım bilgisi.</summary>
    public sealed record LoginResponse(UserProfileDto? User, TwoFactorPrompt? TwoFactor);

    public sealed record AuthFeatures(bool Passkeys);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest body, ISender sender, IAuthCookieService cookies, TwoFactorCookie twoFactor, HttpContext http, CancellationToken ct) =>
            {
                Result<LoginResult> result = await sender.Send(new LoginCommand(body.Tenant, body.Email, body.Password, ClientIp(http)), ct);

                return result.ToHttpResult(login =>
                {
                    if (login.Challenge is { } challenge)
                    {
                        // Oturum henüz açılmadı: bekleyen giriş şifreli HttpOnly cookie'de, istemciye yalnızca yöntem bilgisi.
                        twoFactor.Write(http.Response, challenge);
                        return TypedResults.Ok(new LoginResponse(null, login.Prompt));
                    }

                    AuthResult session = login.Session!;
                    twoFactor.Clear(http.Response);
                    cookies.SetTokens(http.Response, session.AccessToken, session.RefreshToken);
                    return TypedResults.Ok(new LoginResponse(session.User, null));
                });
            })
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("Mağaza, e-posta ve şifre ile giriş. İki adımlı doğrulama açıksa ikinci adım istenir; değilse token'lar cookie'ye yazılır.");

        group.MapPost("/login/two-factor", async (TwoFactorRequest body, ISender sender, IAuthCookieService cookies, TwoFactorCookie twoFactor, HttpContext http, CancellationToken ct) =>
            {
                if (twoFactor.Read(http.Request) is not { } challenge)
                    return AuthErrors.TwoFactorExpired.ToProblem();

                Result<AuthResult> result = await sender.Send(new CompleteTwoFactorLoginCommand(challenge, body.Code, ClientIp(http)), ct);

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
            .WithSummary("İki adımlı girişin ikinci adımı: authenticator uygulamasındaki ya da e-postaya gelen kod.");

        group.MapPost("/login/two-factor/resend", async (ISender sender, TwoFactorCookie twoFactor, HttpContext http, CancellationToken ct) =>
                twoFactor.Read(http.Request) is { } challenge
                    ? await sender.Send(new ResendTwoFactorCodeCommand(challenge), ct).ToHttpResult()
                    : AuthErrors.TwoFactorExpired.ToProblem())
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("İkinci adım e-posta ile yapılıyorsa yeni kod gönderir.");

        bool passkeysEnabled = app.ServiceProvider.GetService<IPasskeyService>() is not null;

        group.MapGet("/features", () => TypedResults.Ok(new AuthFeatures(passkeysEnabled)))
            .AllowAnonymous()
            .WithSummary("Sunucuda açık olan giriş yöntemleri.");

        if (passkeysEnabled)
        {
            group.MapPost("/passkey/options", (IPasskeyService passkeys, PasskeyCeremonyStore ceremonies, HttpContext http) =>
                {
                    // Kullanıcı adı sorulmaz: cihaz bu site için kayıtlı passkey'lerden birini önerir.
                    string json = passkeys.BeginLogin().ToJson();
                    ceremonies.Save(http.Response, PasskeyCeremonyStore.Login, json);
                    return Results.Content(json, "application/json");
                })
                .AllowAnonymous()
                .WithSummary("Passkey ile girişin ilk adımı: tarayıcıya verilecek seçenekler (challenge).");

            group.MapPost("/passkey/login", async (PasskeyLoginRequest body, ISender sender, IAuthCookieService cookies, PasskeyCeremonyStore ceremonies, HttpContext http, CancellationToken ct) =>
                {
                    if (ceremonies.Take(http.Request, http.Response, PasskeyCeremonyStore.Login) is not { } optionsJson)
                        return PasskeyExpired.ToProblem();

                    Result<AuthenticatorAssertionRawResponse> credential = ReadCredential<AuthenticatorAssertionRawResponse>(body.Credential);
                    if (credential.IsFailure)
                        return credential.Errors.ToProblem();

                    Result<AuthResult> result = await sender.Send(
                        new PasskeyLoginCommand(body.Tenant, credential.Value, AssertionOptions.FromJson(optionsJson), ClientIp(http)),
                        ct
                    );

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
                sender
                    .Send(new RegisterCommand(body.Tenant, body.Email, body.Password, body.FirstName, body.LastName, body.CompanyName, body.Phone), ct)
                    .ToHttpResult())
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("Siteden müşteri kaydı; e-postaya 6 haneli doğrulama kodu gönderilir.");

        group.MapPost("/verify-email", async (VerifyEmailRequest body, ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
                (await sender.Send(new VerifyEmailCommand(body.Tenant, body.Email, body.Code, ClientIp(http)), ct)).ToHttpResult(session =>
                {
                    cookies.SetTokens(http.Response, session.AccessToken, session.RefreshToken);
                    return TypedResults.Ok(session.User);
                }))
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("E-posta doğrulama; başarılıysa kullanıcı giriş yapmış olur.");

        group.MapPost("/resend-code", (ResendCodeRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new ResendVerificationCodeCommand(body.Tenant, body.Email), ct).ToHttpResult())
            .AllowAnonymous()
            .RequireRateLimiting(CanRateLimitPolicies.Auth)
            .WithSummary("Yeni doğrulama kodu gönderir.");

        group.MapPost("/refresh", async (ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                if (cookies.GetRefreshToken(http.Request) is not { } refreshToken)
                    return AuthErrors.SessionExpired.ToProblem();

                Result<AuthResult> result = await sender.Send(new RefreshTokenCommand(refreshToken, ClientIp(http)), ct);

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
            .WithSummary("Refresh token cookie'si ile yeni token çifti (rotasyon).");

        group.MapPost("/logout", async (ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                await sender.Send(new LogoutCommand(cookies.GetRefreshToken(http.Request), ClientIp(http)), ct);
                cookies.Clear(http.Response);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .WithSummary("Refresh token'ı iptal eder ve cookie'leri siler.");

        group.MapGet("/me", (ISender sender, CancellationToken ct) => sender.Send(new GetProfileQuery(), ct).ToHttpResult())
            .RequireAuthorization()
            .WithSummary("Giriş yapmış kullanıcının profili.");
    }

    private static readonly Error PasskeyExpired = Error.Unauthorized(AuthErrorCodes.PasskeyFailed, "Passkey isteğinin süresi doldu; tekrar dene.");

    private static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    /// Tarayıcının WebAuthn yanıtını Fido2 tipine çevirir. Uygulamanın JSON ayarları (enum'lar metin vb.) yerine
    /// Fido2'nin kendi öznitelikleri geçerli olsun diye varsayılan ayarlarla okunur.
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
