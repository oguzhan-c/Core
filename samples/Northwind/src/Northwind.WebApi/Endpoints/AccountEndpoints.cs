using System.Text.Json;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.WebApi;
using Can.Core.Security.Passkeys;
using Fido2NetLib;
using Northwind.Application.Features.Account;
using Northwind.Application.Features.Auth;
using Northwind.WebApi.Security;

namespace Northwind.WebApi.Endpoints;

/// <summary>Giriş yapmış kullanıcının hesap güvenliği: iki adımlı doğrulama (authenticator uygulaması / e-posta) ve passkey'ler.</summary>
internal static class AccountEndpoints
{
    public sealed record CodeRequest(string Code);

    public sealed record PasswordRequest(string Password);

    public sealed record NameRequest(string Name);

    /// <param name="Credential">Tarayıcının <c>navigator.credentials.create()</c> yanıtı (JSON).</param>
    public sealed record AddPasskeyRequest(string? Name, JsonElement Credential);

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/account").WithTags("Account").RequireAuthorization();

        group.MapGet("/security", (ISender sender, CancellationToken ct) => sender.Send(new GetAccountSecurityQuery(), ct).ToHttpResult())
            .WithSummary("İki adımlı doğrulama durumu ve kayıtlı passkey'ler.");

        // ---------------------------------------------------------------- iki adımlı doğrulama

        group.MapPost("/two-factor/otp/setup", (ISender sender, CancellationToken ct) => sender.Send(new BeginOtpSetupCommand(), ct).ToHttpResult())
            .WithSummary("Authenticator uygulaması kurulumu: QR kodu adresi ve elle girilecek anahtar.");

        group.MapPost("/two-factor/otp/enable", (CodeRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new EnableOtpCommand(body.Code), ct).ToHttpResult())
            .WithSummary("Uygulamadaki ilk kod ile kurulumu tamamlar; girişte artık bu kod istenir.");

        group.MapPost("/two-factor/email/enable", (ISender sender, CancellationToken ct) =>
                sender.Send(new EnableEmailTwoFactorCommand(), ct).ToHttpResult())
            .WithSummary("Girişte e-postaya gönderilen kod istenir.");

        group.MapPost("/two-factor/disable", (PasswordRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new DisableTwoFactorCommand(body.Password), ct).ToHttpResult())
            .WithSummary("İki adımlı doğrulamayı kapatır (şifre gerekir).");

        // ---------------------------------------------------------------- passkey'ler

        if (app.ServiceProvider.GetService<IPasskeyService>() is null)
            return;

        group.MapPost("/passkeys/options", async (ISender sender, PasskeyCeremonyStore ceremonies, HttpContext http, CancellationToken ct) =>
                (await sender.Send(new BeginPasskeyRegistrationCommand(), ct)).ToHttpResult(options =>
                {
                    string json = options.ToJson();
                    ceremonies.Save(http.Response, PasskeyCeremonyStore.Register, json);
                    return Results.Content(json, "application/json");
                }))
            .WithSummary("Passkey eklemenin ilk adımı: tarayıcıya verilecek seçenekler.");

        group.MapPost("/passkeys", async (AddPasskeyRequest body, ISender sender, PasskeyCeremonyStore ceremonies, HttpContext http, CancellationToken ct) =>
            {
                if (ceremonies.Take(http.Request, http.Response, PasskeyCeremonyStore.Register) is not { } optionsJson)
                    return Error.Failure(AuthErrorCodes.PasskeyFailed, "Passkey isteğinin süresi doldu; tekrar dene.").ToProblem();

                Result<AuthenticatorAttestationRawResponse> credential = AuthEndpoints.ReadCredential<AuthenticatorAttestationRawResponse>(body.Credential);
                if (credential.IsFailure)
                    return credential.Errors.ToProblem();

                return await sender
                    .Send(new CompletePasskeyRegistrationCommand(credential.Value, CredentialCreateOptions.FromJson(optionsJson), body.Name), ct)
                    .ToHttpResult();
            })
            .WithSummary("Passkey eklemenin ikinci adımı: cihazın yanıtı doğrulanır ve passkey kaydedilir.");

        group.MapPut("/passkeys/{id:guid}", (Guid id, NameRequest body, ISender sender, CancellationToken ct) =>
                sender.Send(new RenamePasskeyCommand(id, body.Name), ct).ToHttpResult())
            .WithSummary("Passkey'in adını değiştirir.");

        group.MapDelete("/passkeys/{id:guid}", (Guid id, ISender sender, CancellationToken ct) =>
                sender.Send(new DeletePasskeyCommand(id), ct).ToHttpResult())
            .WithSummary("Passkey'i siler; o cihazla artık giriş yapılamaz.");
    }
}
