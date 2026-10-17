using System.Text.Json;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Security.Passkeys;
using Can.Core.WebApi;
using Can.Core.WebApi.Caching;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.RateLimiting;
using Fido2NetLib;
using CanAppTemplate.Application.Features.Account;
using CanAppTemplate.Application.Features.Auth;
using CanAppTemplate.WebApi.Security;

namespace CanAppTemplate.WebApi.Endpoints;

/// <summary>
/// Giriş yapmış kullanıcının hesabı: profil, şifre, iki adımlı doğrulama, kurtarma kodları, passkey'ler ve dış hesaplar.
/// <c>/api/auth/account</c> altında: güvenlik damgasını değiştiren işlemlerden sonra AYNI istekte bu oturuma yeni
/// token verilir, bunun için refresh token cookie'si (yolu <c>/api/auth</c>) bu adreslere de gönderilir.
/// Oturum ve cihaz listesi core'un <c>/api/sessions</c> uçlarındadır.
/// </summary>
internal static class AccountEndpoints
{
    public sealed record CodeRequest(string Code);

    public sealed record PasswordRequest(string Password);

    public sealed record OptionalPasswordRequest(string? Password);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string NewPassword);

    public sealed record NameRequest(string Name);

    public sealed record ProfileRequest(string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent);

#if (Passkey)
    /// <param name="Credential">Tarayıcının <c>navigator.credentials.create()</c> yanıtı (JSON).</param>
    public sealed record AddPasskeyRequest(string? Name, JsonElement Credential);
#endif

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/auth/account").WithTags("Account").RequireAuthorization().WithNoStore();

        group.MapPut("/profile", (ProfileRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new UpdateProfileCommand(body.FirstName, body.LastName, body.PhoneNumber, body.MarketingConsent), ct).ToHttpResult())
            .WithSummary("Ad, soyad, telefon ve kampanya izni.");

        group.MapGet("/security", (ISender sender, CancellationToken ct) => sender.Send(new GetAccountSecurityQuery(), ct).ToHttpResult())
            .WithSummary("Şifre, iki adımlı doğrulama, kurtarma kodları, passkey'ler ve bağlı dış hesaplar.");

        group.MapPost("/password", async (ChangePasswordRequest body, ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
                await ThenReissueAsync(await sender.Send(new ChangePasswordCommand(body.CurrentPassword, body.NewPassword), ct), sender, clients, cookies, http, ct))
            .RequireRateLimiting(CanRateLimitPolicies.Sensitive)
            .WithSummary("Şifre değiştirir (şifresiz hesapta şifre belirler). Diğer cihazlardaki oturumlar kapanır.");

#if (TwoFactor)
        // ---------------------------------------------------------------- iki adımlı doğrulama
#endif

#if (Otp)
        group.MapPost("/two-factor/otp/setup", (ISender sender, CancellationToken ct) => sender.Send(new BeginOtpSetupCommand(), ct).ToHttpResult())
            .WithSummary("Authenticator uygulaması kurulumu: QR kodu adresi ve elle girilecek anahtar.");

        group.MapPost("/two-factor/otp/enable", async (CodeRequest body, ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
                await ThenReissueAsync(await sender.Send(new EnableOtpCommand(body.Code), ct), sender, clients, cookies, http, ct))
            .RequireRateLimiting(CanRateLimitPolicies.Sensitive)
            .WithSummary("Uygulamadaki ilk kod ile kurulumu tamamlar; kurtarma kodları bir kez döner.");
#endif

#if (EmailTwoFactor)
        group.MapPost("/two-factor/email/enable", async (ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
                await ThenReissueAsync(await sender.Send(new EnableEmailTwoFactorCommand(), ct), sender, clients, cookies, http, ct))
            .WithSummary("Girişte e-postaya gönderilen kod istenir; kurtarma kodları bir kez döner.");
#endif

#if (TwoFactor)
        group.MapPost("/two-factor/disable", async (PasswordRequest body, ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
                await ThenReissueAsync(await sender.Send(new DisableTwoFactorCommand(body.Password), ct), sender, clients, cookies, http, ct))
            .RequireRateLimiting(CanRateLimitPolicies.Sensitive)
            .WithSummary("İki adımlı doğrulamayı kapatır (şifre gerekir).");

        group.MapPost("/two-factor/recovery-codes", (OptionalPasswordRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new RegenerateRecoveryCodesCommand(body.Password), ct).ToHttpResult())
            .RequireRateLimiting(CanRateLimitPolicies.Sensitive)
            .WithSummary("Kurtarma kodlarını yeniler; eskiler geçersiz olur.");
#endif

#if (ExternalLogin)
        // Dış hesap bağlamak sağlayıcıya yönlendirme gerektirir: GET /api/auth/external/{provider}/link
        group.MapDelete("/external-logins/{provider}", async (string provider, ISender sender, ClientInfoFactory clients, IAuthCookieService cookies, HttpContext http, CancellationToken ct) =>
                await ThenReissueAsync(await sender.Send(new UnlinkExternalLoginCommand(provider), ct), sender, clients, cookies, http, ct))
            .WithSummary("Bağlı dış hesabı kaldırır; hesaba girmenin tek yolu buysa kaldırılmaz.");
#endif

#if (Passkey)
        // ---------------------------------------------------------------- passkey'ler

        if (app.ServiceProvider.GetService<IPasskeyService>() is null)
            return;

        group.MapPost("/passkeys/options", async (ISender sender, PasskeyCeremonyStore ceremonies, HttpContext http, CancellationToken ct) =>
            {
                Result<CredentialCreateOptions> options = await sender.Send(new BeginPasskeyRegistrationCommand(), ct);
                if (options.IsFailure)
                    return options.Errors.ToProblem();

                string json = options.Value.ToJson();
                await ceremonies.SaveAsync(http.Response, PasskeyCeremonyStore.Register, json, ct);
                return Results.Content(json, "application/json");
            })
            .WithSummary("Passkey eklemenin ilk adımı: tarayıcıya verilecek seçenekler.");

        group.MapPost("/passkeys", async (AddPasskeyRequest body, ISender sender, PasskeyCeremonyStore ceremonies, HttpContext http, CancellationToken ct) =>
            {
                if (await ceremonies.TakeAsync(http.Request, http.Response, PasskeyCeremonyStore.Register, ct) is not { } optionsJson)
                    return Error.Failure(AuthErrorCodes.PasskeyFailed, "Passkey isteğinin süresi doldu; tekrar dene.").ToProblem();

                Result<AuthenticatorAttestationRawResponse> credential = AuthEndpoints.ReadCredential<AuthenticatorAttestationRawResponse>(body.Credential);
                if (credential.IsFailure)
                    return credential.Errors.ToProblem();

                return await sender.Send(new CompletePasskeyRegistrationCommand(credential.Value, CredentialCreateOptions.FromJson(optionsJson), body.Name), ct).ToHttpResult();
            })
            .WithSummary("Passkey eklemenin ikinci adımı: cihazın yanıtı doğrulanır ve passkey kaydedilir.");

        group.MapPut("/passkeys/{id:guid}", (Guid id, NameRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new RenamePasskeyCommand(id, body.Name), ct).ToHttpResult())
            .WithSummary("Passkey'in adını değiştirir.");

        group.MapDelete("/passkeys/{id:guid}", (Guid id, ISender sender, CancellationToken ct) =>
                sender.Send(new DeletePasskeyCommand(id), ct).ToHttpResult())
            .WithSummary("Passkey'i siler; hesaba girmenin tek yolu buysa silinmez.");
#endif
    }

    /// <summary>
    /// Damgayı değiştiren işlem başarılıysa bu oturuma yeni token'lar yazılır (diğer cihazlar kapanır). Yanıt gövdesi
    /// işlemin sonucudur (ör. kurtarma kodları).
    /// </summary>
    private static async Task<IResult> ThenReissueAsync<T>(
        Result<T> result,
        ISender sender,
        ClientInfoFactory clients,
        IAuthCookieService cookies,
        HttpContext http,
        CancellationToken ct)
    {
        if (result.IsFailure)
            return result.ToHttpResult();

        Result<AuthResult> session = await sender.Send(new ReissueCurrentSessionCommand(cookies.GetRefreshToken(http.Request), clients.Create(http)), ct);
        if (session.IsSuccess)
            cookies.SetTokens(http.Response, session.Value.AccessToken, session.Value.RefreshToken);
        else
            cookies.Clear(http.Response); // bu oturum da kapandı: istemci giriş sayfasına gider

        return result.ToHttpResult();
    }
}
