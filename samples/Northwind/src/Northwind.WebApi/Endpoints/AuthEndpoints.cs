using System.Text.Json;
using Can.Core.Application.Exceptions;
using Can.Core.Domain.Exceptions;
using Can.Core.Mediator;
using Can.Core.Security.Passkeys;
using Can.Core.WebApi.DependencyInjection;
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
                LoginResult result = await sender.Send(new LoginCommand(body.Tenant, body.Email, body.Password, ClientIp(http)), ct);

                if (result.Challenge is { } challenge)
                {
                    // Oturum henüz açılmadı: bekleyen giriş şifreli HttpOnly cookie'de, istemciye yalnızca yöntem bilgisi.
                    twoFactor.Write(http.Response, challenge);
                    return TypedResults.Ok(new LoginResponse(null, result.Prompt));
                }

                AuthResult session = result.Session!;
                twoFactor.Clear(http.Response);
                cookies.SetTokens(http.Response, session.AccessToken, session.RefreshToken);
                return TypedResults.Ok(new LoginResponse(session.User, null));
            })
            .AllowAnonymous()
            .WithSummary("Mağaza, e-posta ve şifre ile giriş. İki adımlı doğrulama açıksa ikinci adım istenir; değilse token'lar cookie'ye yazılır.");

        group.MapPost("/login/two-factor", async (TwoFactorRequest body, ISender sender, IAuthCookieService cookies, TwoFactorCookie twoFactor, HttpContext http, CancellationToken ct) =>
            {
                TwoFactorChallenge challenge = twoFactor.Read(http.Request) ?? throw TwoFactorExpired();

                try
                {
                    AuthResult result = await sender.Send(new CompleteTwoFactorLoginCommand(challenge, body.Code, ClientIp(http)), ct);
                    twoFactor.Clear(http.Response);
                    cookies.SetTokens(http.Response, result.AccessToken, result.RefreshToken);
                    return TypedResults.Ok(result.User);
                }
                catch (UnauthorizedException)
                {
                    // Kilitlenme ya da geçersiz bekleyen giriş: baştan giriş yapılmalı.
                    twoFactor.Clear(http.Response);
                    throw;
                }
            })
            .AllowAnonymous()
            .WithSummary("İki adımlı girişin ikinci adımı: authenticator uygulamasındaki ya da e-postaya gelen kod.");

        group.MapPost("/login/two-factor/resend", async (ISender sender, TwoFactorCookie twoFactor, HttpContext http, CancellationToken ct) =>
            {
                TwoFactorChallenge challenge = twoFactor.Read(http.Request) ?? throw TwoFactorExpired();
                await sender.Send(new ResendTwoFactorCodeCommand(challenge), ct);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
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
                    string optionsJson =
                        ceremonies.Take(http.Request, http.Response, PasskeyCeremonyStore.Login)
                        ?? throw new UnauthorizedException("Passkey isteğinin süresi doldu; tekrar dene.") { Code = AuthErrorCodes.PasskeyFailed };

                    AuthenticatorAssertionRawResponse credential = ReadCredential<AuthenticatorAssertionRawResponse>(body.Credential);
                    AuthResult result = await sender.Send(
                        new PasskeyLoginCommand(body.Tenant, credential, AssertionOptions.FromJson(optionsJson), ClientIp(http)),
                        ct
                    );

                    cookies.SetTokens(http.Response, result.AccessToken, result.RefreshToken);
                    return TypedResults.Ok(result.User);
                })
                .AllowAnonymous()
                .WithSummary("Passkey ile girişin ikinci adımı: cihazın imzası doğrulanır, token'lar cookie'ye yazılır.");
        }

        group.MapPost("/register", async (RegisterRequest body, ISender sender, CancellationToken ct) =>
                TypedResults.Ok(
                    await sender.Send(
                        new RegisterCommand(body.Tenant, body.Email, body.Password, body.FirstName, body.LastName, body.CompanyName, body.Phone),
                        ct
                    )
                ))
            .AllowAnonymous()
            .WithSummary("Siteden müşteri kaydı; e-postaya 6 haneli doğrulama kodu gönderilir.");

        group.MapPost("/verify-email", async (VerifyEmailRequest body, ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                AuthResult result = await sender.Send(new VerifyEmailCommand(body.Tenant, body.Email, body.Code, ClientIp(http)), ct);
                cookies.SetTokens(http.Response, result.AccessToken, result.RefreshToken);
                return TypedResults.Ok(result.User);
            })
            .AllowAnonymous()
            .WithSummary("E-posta doğrulama; başarılıysa kullanıcı giriş yapmış olur.");

        group.MapPost("/resend-code", async (ResendCodeRequest body, ISender sender, CancellationToken ct) =>
            {
                await sender.Send(new ResendVerificationCodeCommand(body.Tenant, body.Email), ct);
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .WithSummary("Yeni doğrulama kodu gönderir.");

        group.MapPost("/refresh", async (ISender sender, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
            {
                string refreshToken = cookies.GetRefreshToken(http.Request) ?? throw new UnauthorizedException("Oturum bulunamadı.");

                try
                {
                    AuthResult result = await sender.Send(new RefreshTokenCommand(refreshToken, ClientIp(http)), ct);
                    cookies.SetTokens(http.Response, result.AccessToken, result.RefreshToken);
                    return TypedResults.Ok(result.User);
                }
                catch (UnauthorizedException)
                {
                    // Geçersiz/çalınmış token: tarayıcıdaki cookie'leri de temizle.
                    cookies.Clear(http.Response);
                    throw;
                }
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

        group.MapGet("/me", (ISender sender, CancellationToken ct) => sender.Send(new GetProfileQuery(), ct))
            .RequireAuthorization()
            .WithSummary("Giriş yapmış kullanıcının profili.");
    }

    private static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static UnauthorizedException TwoFactorExpired() =>
        new("Doğrulama süresi doldu; tekrar giriş yap.") { Code = AuthErrorCodes.TwoFactorExpired };

    /// <summary>
    /// Tarayıcının WebAuthn yanıtını Fido2 tipine çevirir. Uygulamanın JSON ayarları (enum'lar metin vb.) yerine
    /// Fido2'nin kendi öznitelikleri geçerli olsun diye varsayılan ayarlarla okunur.
    /// </summary>
    internal static T ReadCredential<T>(JsonElement credential)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(credential.GetRawText()) ?? throw InvalidCredential();
        }
        catch (JsonException)
        {
            throw InvalidCredential();
        }

        static BusinessException InvalidCredential() => new("Geçersiz passkey yanıtı.") { Code = AuthErrorCodes.PasskeyFailed };
    }
}
