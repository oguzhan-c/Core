using System.Security.Cryptography;
using CanFx.Security.Entities;
using CanFx.WebApi.DependencyInjection;
using CanAppTemplate.Application.Services.AuthService;

namespace CanAppTemplate.WebAPI.Security;

/// <summary>
/// İstemci bilgisi (oturum listesi ve "yeni cihazdan giriş" için). Tarayıcıya ilk girişte rastgele bir cihaz kimliği
/// HttpOnly cookie olarak verilir (1 yıl): kullanıcı aynı tarayıcıdan döndüğünde cihaz tanınır. Mobil uygulama kendi
/// cihaz kimliğini ve adını istek gövdesinde gönderir. Bu bilgiler yalnızca gösterim ve uyarı içindir; yetki kararı
/// verilmez (yetki imzalı token'dan gelir).
/// </summary>
public sealed class ClientInfoFactory
{
    private const string DeviceCookie = "pz_device";
    private static readonly TimeSpan DeviceCookieLifetime = TimeSpan.FromDays(365);

    private readonly AuthCookieOptions _cookieOptions;

    public ClientInfoFactory(AuthCookieOptions cookieOptions) => _cookieOptions = cookieOptions;

    public ClientInfo Create(HttpContext http, string? deviceId = null, string? deviceName = null)
    {
        string? userAgent = http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
        string? id = Clean(deviceId, SessionDevice.DeviceIdMaxLength) ?? BrowserDeviceId(http);
        string? name = Clean(deviceName, SessionDevice.DeviceNameMaxLength) ?? DescribeUserAgent(userAgent);
        return new ClientInfo(http.Connection.RemoteIpAddress?.ToString(), userAgent, id, name);
    }

    private string BrowserDeviceId(HttpContext http)
    {
        if (http.Request.Cookies.TryGetValue(DeviceCookie, out string? existing) && existing is { Length: 32 } && existing.All(char.IsAsciiHexDigit))
            return existing;

        string created = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        http.Response.Cookies.Append(
            DeviceCookie,
            created,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = _cookieOptions.Secure,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = DeviceCookieLifetime,
                IsEssential = true,
            }
        );
        return created;
    }

    private static string? Clean(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];

    /// <summary>"Chrome · macOS" gibi kısa ad (kabaca; yalnızca listede gösterilir).</summary>
    internal static string? DescribeUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return null;

        string browser =
            userAgent.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : userAgent.Contains("OPR/", StringComparison.Ordinal) ? "Opera"
            : userAgent.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : userAgent.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome"
            : userAgent.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : "Tarayıcı";

        string os =
            userAgent.Contains("iPhone", StringComparison.Ordinal) || userAgent.Contains("iPad", StringComparison.Ordinal) ? "iOS"
            : userAgent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : userAgent.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS"
            : userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : userAgent.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : "?";

        return $"{browser} · {os}";
    }
}
