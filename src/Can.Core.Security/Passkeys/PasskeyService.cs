using Fido2NetLib;
using Fido2NetLib.Objects;

namespace Can.Core.Security.Passkeys;

/// <summary>Passkey (WebAuthn) ayarları.</summary>
public sealed class PasskeyOptions
{
    /// <summary>Relying party kimliği: sitenin alan adı, ör. <c>"example.com"</c> (port ve şema olmadan).</summary>
    public string ServerDomain { get; set; } = string.Empty;

    /// <summary>Kullanıcıya gösterilen ad, ör. <c>"Can App"</c>.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>İzin verilen origin'ler, ör. <c>["https://example.com", "https://app.example.com"]</c>.</summary>
    public HashSet<string> Origins { get; set; } = [];
}

/// <summary>Passkey sahibi kullanıcı. <see cref="UserHandle"/> e-posta gibi kişisel veri içermemeli (ör. rastgele 32 bayt).</summary>
public sealed record PasskeyUser(byte[] UserHandle, string Name, string DisplayName);

/// <summary>Kayıt başarıyla tamamlandığında saklanacak bilgiler (<c>UserPasskey</c> entity'sine yazılır).</summary>
public sealed record PasskeyCredential(
    byte[] CredentialId,
    byte[] PublicKey,
    byte[] UserHandle,
    uint SignCount,
    Guid AaGuid,
    string? Transports,
    bool IsBackedUp);

/// <summary>Giriş doğrulandığında: hangi passkey kullanıldı ve yeni imza sayacı.</summary>
public sealed record PasskeyAssertion(byte[] CredentialId, uint SignCount);

/// <summary>
/// Passkey kayıt ve giriş akışı. Her akış iki adımdır:
/// <list type="number">
/// <item><c>Begin...</c>: tarayıcıya gönderilecek seçenekleri üretir. Seçenekleri (<c>options.ToJson()</c>)
/// kısa süreliğine sunucuda sakla (cache/session); içindeki challenge tek kullanımlıktır.</item>
/// <item><c>Complete...</c>: tarayıcının yanıtını saklanan seçeneklerle doğrular
/// (<c>CredentialCreateOptions.FromJson(json)</c> / <c>AssertionOptions.FromJson(json)</c>).</item>
/// </list>
/// Doğrulama başarısız olursa Fido2NetLib <c>Fido2VerificationException</c> fırlatır.
/// </summary>
public interface IPasskeyService
{
    CredentialCreateOptions BeginRegistration(PasskeyUser user, IEnumerable<byte[]> existingCredentialIds);

    Task<PasskeyCredential> CompleteRegistrationAsync(
        AuthenticatorAttestationRawResponse response,
        CredentialCreateOptions options,
        Func<byte[], CancellationToken, Task<bool>> isCredentialIdUnique,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="allowedCredentialIds"/> boşsa kullanıcı adı sorulmadan giriş yapılır
    /// (cihaz, kayıtlı passkey'lerden birini önerir).
    /// </summary>
    AssertionOptions BeginLogin(IEnumerable<byte[]>? allowedCredentialIds = null);

    Task<PasskeyAssertion> CompleteLoginAsync(
        AuthenticatorAssertionRawResponse response,
        AssertionOptions options,
        byte[] storedPublicKey,
        uint storedSignCount,
        Func<byte[], byte[], CancellationToken, Task<bool>> isUserHandleOwnerOfCredential,
        CancellationToken cancellationToken = default);
}

/// <summary>Fido2NetLib (MIT) üzerinde ince bir katman.</summary>
public sealed class PasskeyService : IPasskeyService
{
    private readonly IFido2 _fido2;

    public PasskeyService(IFido2 fido2)
    {
        _fido2 = fido2;
    }

    public CredentialCreateOptions BeginRegistration(PasskeyUser user, IEnumerable<byte[]> existingCredentialIds)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(existingCredentialIds);

        return _fido2.RequestNewCredential(
            new RequestNewCredentialParams
            {
                User = new Fido2User
                {
                    Id = user.UserHandle,
                    Name = user.Name,
                    DisplayName = user.DisplayName,
                },
                ExcludeCredentials = existingCredentialIds.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Required,
                    UserVerification = UserVerificationRequirement.Preferred,
                },
                AttestationPreference = AttestationConveyancePreference.None,
                Extensions = new AuthenticationExtensionsClientInputs { CredProps = true },
            }
        );
    }

    public async Task<PasskeyCredential> CompleteRegistrationAsync(
        AuthenticatorAttestationRawResponse response,
        CredentialCreateOptions options,
        Func<byte[], CancellationToken, Task<bool>> isCredentialIdUnique,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isCredentialIdUnique);

        RegisteredPublicKeyCredential credential = await _fido2
            .MakeNewCredentialAsync(
                new MakeNewCredentialParams
                {
                    AttestationResponse = response,
                    OriginalOptions = options,
                    IsCredentialIdUniqueToUserCallback = (args, token) => isCredentialIdUnique(args.CredentialId, token),
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        string? transports = credential.Transports is { } values && values.Any() ? string.Join(',', values) : null;

        return new PasskeyCredential(
            credential.Id,
            credential.PublicKey,
            credential.User.Id,
            credential.SignCount,
            credential.AaGuid,
            transports,
            credential.IsBackedUp
        );
    }

    public AssertionOptions BeginLogin(IEnumerable<byte[]>? allowedCredentialIds = null) =>
        _fido2.GetAssertionOptions(
            new GetAssertionOptionsParams
            {
                AllowedCredentials = (allowedCredentialIds ?? []).Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
                UserVerification = UserVerificationRequirement.Preferred,
            }
        );

    public async Task<PasskeyAssertion> CompleteLoginAsync(
        AuthenticatorAssertionRawResponse response,
        AssertionOptions options,
        byte[] storedPublicKey,
        uint storedSignCount,
        Func<byte[], byte[], CancellationToken, Task<bool>> isUserHandleOwnerOfCredential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storedPublicKey);
        ArgumentNullException.ThrowIfNull(isUserHandleOwnerOfCredential);

        VerifyAssertionResult result = await _fido2
            .MakeAssertionAsync(
                new MakeAssertionParams
                {
                    AssertionResponse = response,
                    OriginalOptions = options,
                    StoredPublicKey = storedPublicKey,
                    StoredSignatureCounter = storedSignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, token) =>
                        isUserHandleOwnerOfCredential(args.UserHandle, args.CredentialId, token),
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        return new PasskeyAssertion(result.CredentialId, result.SignCount);
    }
}
