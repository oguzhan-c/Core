using System.Security.Cryptography;
using System.Text.Json;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using CanAppTemplate.Application.Features.Auth;

namespace CanAppTemplate.WebApi.Security;

/// <summary>
/// Şifre doğrulandıktan sonra ikinci adım beklenirken "yarım giriş" bilgisi. İstemciye ŞİFRELİ ve imzalı (ASP.NET
/// Data Protection), 10 dakika geçerli, HttpOnly bir cookie olarak verilir: JavaScript okuyamaz, istemci içeriğini
/// değiştiremez, header'dan okunmaz.
/// </summary>
internal sealed class TwoFactorCookie
{
    private const string CookieName = "pz_2fa";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly AuthCookieOptions _cookieOptions;

    public TwoFactorCookie(IDataProtectionProvider dataProtection, AuthCookieOptions cookieOptions)
    {
        _protector = dataProtection.CreateProtector("CanAppTemplate.TwoFactorLogin").ToTimeLimitedDataProtector();
        _cookieOptions = cookieOptions;
    }

    public void Write(HttpResponse response, TwoFactorChallenge challenge)
    {
        string payload = _protector.Protect(JsonSerializer.Serialize(challenge), Lifetime);
        response.Cookies.Append(CookieName, payload, CookieOptions(Lifetime));
    }

    /// <summary>Cookie yoksa, süresi dolmuşsa ya da kurcalanmışsa <see langword="null"/>.</summary>
    public TwoFactorChallenge? Read(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out string? payload) || string.IsNullOrEmpty(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TwoFactorChallenge>(_protector.Unprotect(payload));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Clear(HttpResponse response) => response.Cookies.Delete(CookieName, CookieOptions(null));

    private CookieOptions CookieOptions(TimeSpan? maxAge) =>
        new()
        {
            HttpOnly = true,
            Secure = _cookieOptions.Secure,
            SameSite = _cookieOptions.SameSite,
            Path = "/api/auth",
            MaxAge = maxAge,
            IsEssential = true,
        };
}

/// <summary>
/// Passkey (WebAuthn) akışlarının ilk adımında üretilen seçenekler (içinde tek kullanımlık challenge) sunucuda 5 dakika
/// saklanır; tarayıcıya yalnızca rastgele bir anahtar HttpOnly cookie olarak verilir. İkinci adımda seçenekler bir kez
/// okunup silinir. <see cref="IDistributedCache"/>: tek sunucuda bellek, Redis varsa sunucular arası ortak.
/// </summary>
internal sealed class PasskeyCeremonyStore
{
    public const string Login = "login";
    public const string Register = "register";

    private const string CookiePrefix = "pz_passkey_";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly IDistributedCache _cache;
    private readonly AuthCookieOptions _cookieOptions;

    public PasskeyCeremonyStore(IDistributedCache cache, AuthCookieOptions cookieOptions)
    {
        _cache = cache;
        _cookieOptions = cookieOptions;
    }

    public async Task SaveAsync(HttpResponse response, string purpose, string optionsJson, CancellationToken cancellationToken)
    {
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await _cache.SetStringAsync(CacheKey(purpose, key), optionsJson, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime }, cancellationToken);
        response.Cookies.Append(CookiePrefix + purpose, key, CookieOptions(Lifetime));
    }

    /// <summary>Seçenekleri bir kez döndürür ve siler; yoksa ya da süresi dolduysa <see langword="null"/>.</summary>
    public async Task<string?> TakeAsync(HttpRequest request, HttpResponse response, string purpose, CancellationToken cancellationToken)
    {
        response.Cookies.Delete(CookiePrefix + purpose, CookieOptions(null));

        if (!request.Cookies.TryGetValue(CookiePrefix + purpose, out string? key) || string.IsNullOrEmpty(key))
            return null;

        string cacheKey = CacheKey(purpose, key);
        string? optionsJson = await _cache.GetStringAsync(cacheKey, cancellationToken);
        if (optionsJson is not null)
            await _cache.RemoveAsync(cacheKey, cancellationToken);

        return optionsJson;
    }

    private static string CacheKey(string purpose, string key) => $"passkey:{purpose}:{key}";

    private CookieOptions CookieOptions(TimeSpan? maxAge) =>
        new()
        {
            HttpOnly = true,
            Secure = _cookieOptions.Secure,
            SameSite = _cookieOptions.SameSite,
            Path = "/api",
            MaxAge = maxAge,
            IsEssential = true,
        };
}
