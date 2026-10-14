using System.Globalization;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Can.Core.WebApi.Security;

/// <summary>
/// <c>AddCanSecurityHeaders(...)</c> ayarları. Her başlık <see langword="null"/> yapılarak kapatılabilir; uç noktanın
/// kendi verdiği başlık ezilmez.
/// </summary>
public sealed class SecurityHeadersOptions
{
    /// <summary>JSON dönen API için en sıkı politika: hiçbir kaynak yüklenmez, sayfa çerçeveye gömülemez.</summary>
    public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>
    /// <c>/docs</c> (Scalar) için: betik ve stil CDN'den, istekler aynı origin'e. Arayüz kendi sunucundan veriliyorsa
    /// sıkılaştır.
    /// </summary>
    public const string ApiReferenceContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; "
        + "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net; img-src 'self' data: https:; connect-src 'self'; frame-ancestors 'none'";

    /// <summary>
    /// <c>Strict-Transport-Security</c>: tarayıcı siteye bu süre boyunca yalnızca HTTPS ile gelir (ilk istekteki HTTP'nin
    /// ele geçirilmesini önler). Yalnızca HTTPS yanıtlarına ve Development dışında eklenir. <see langword="null"/>: kapalı.
    /// </summary>
    public TimeSpan? HstsMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>HSTS alt alan adlarını da kapsar. Tüm alt alan adları HTTPS değilse kapat.</summary>
    public bool HstsIncludeSubDomains { get; set; } = true;

    /// <summary>
    /// HSTS preload listesine aday (<c>preload</c>). Tarayıcılara gömülür ve geri alınması aylar sürer; ancak emin olunca aç.
    /// </summary>
    public bool HstsPreload { get; set; }

    /// <summary><c>X-Content-Type-Options</c>: tarayıcı içerik türünü tahmin etmez (yüklenen "resim" script olarak çalışmaz).</summary>
    public string? ContentTypeOptions { get; set; } = "nosniff";

    /// <summary><c>X-Frame-Options</c>: sayfa başka sitenin çerçevesine gömülemez (clickjacking). Eski tarayıcılar için; yenileri CSP <c>frame-ancestors</c>'a bakar.</summary>
    public string? FrameOptions { get; set; } = "DENY";

    /// <summary><c>Content-Security-Policy</c>: varsayılan politika (API için en sıkısı).</summary>
    public string? ContentSecurityPolicy { get; set; } = ApiContentSecurityPolicy;

    /// <summary>
    /// Yol ön ekine göre CSP (en uzun eşleşen kazanır). Aynı sunucudan verilen SPA ya da belge arayüzü için gevşek
    /// politika burada verilir. Varsayılan: <c>/docs</c> → <see cref="ApiReferenceContentSecurityPolicy"/>.
    /// </summary>
    public IDictionary<string, string> ContentSecurityPolicyByPath { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["/docs"] = ApiReferenceContentSecurityPolicy,
    };

    /// <summary>
    /// CSP yalnızca raporlansın, uygulanmasın (<c>Content-Security-Policy-Report-Only</c>). Yeni politikayı canlıda
    /// kırmadan denemek için.
    /// </summary>
    public bool ContentSecurityPolicyReportOnly { get; set; }

    /// <summary><c>Referrer-Policy</c>: başka siteye giderken adresin tamamı (içindeki token'lar) gönderilmez.</summary>
    public string? ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary><c>Permissions-Policy</c>: kamera, mikrofon, konum, ödeme ... kapalı.</summary>
    public string? PermissionsPolicy { get; set; } = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    /// <summary><c>Cross-Origin-Opener-Policy</c>: başka site bu pencereye referansla erişemez.</summary>
    public string? CrossOriginOpenerPolicy { get; set; } = "same-origin";

    /// <summary>
    /// <c>Cross-Origin-Resource-Policy</c>: yanıtlar başka sitelerce (<c>&lt;img&gt;</c>, <c>&lt;script&gt;</c>) gömülemez.
    /// Dosyaları başka alan adındaki ön yüz gösteriyorsa <c>same-site</c> ya da <c>cross-origin</c> yap.
    /// </summary>
    public string? CrossOriginResourcePolicy { get; set; } = "same-origin";

    /// <summary><c>X-XSS-Protection: 0</c>: eski tarayıcı filtresi kapatılır (kendisi açık oluşturuyordu).</summary>
    public bool DisableLegacyXssFilter { get; set; } = true;

    /// <summary><c>Server</c> ve <c>X-Powered-By</c> başlıkları kaldırılır (sunucu yazılımı ve sürümü gösterilmez).</summary>
    public bool RemoveServerHeaders { get; set; } = true;

    internal string? HstsValue()
    {
        if (HstsMaxAge is not { } maxAge || maxAge <= TimeSpan.Zero)
            return null;
        string value = "max-age=" + ((long)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        if (HstsIncludeSubDomains)
            value += "; includeSubDomains";
        if (HstsPreload)
            value += "; preload";
        return value;
    }
}

internal sealed class SecurityHeadersMiddleware(RequestDelegate next, SecurityHeadersOptions options, IHostEnvironment environment)
{
    private readonly string? _hsts = options.HstsValue();
    private readonly KeyValuePair<string, string>[] _cspByPath = options.ContentSecurityPolicyByPath
        .OrderByDescending(p => p.Key.Length)
        .ToArray();

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            (SecurityHeadersMiddleware self, HttpContext http) = ((SecurityHeadersMiddleware, HttpContext))state;
            self.Apply(http);
            return Task.CompletedTask;
        }, (this, context));
        return next(context);
    }

    private void Apply(HttpContext context)
    {
        IHeaderDictionary headers = context.Response.Headers;

        if (_hsts is not null && context.Request.IsHttps && !environment.IsDevelopment())
            Set(headers, "Strict-Transport-Security", _hsts);

        Set(headers, "X-Content-Type-Options", options.ContentTypeOptions);
        Set(headers, "X-Frame-Options", options.FrameOptions);
        Set(headers, "Referrer-Policy", options.ReferrerPolicy);
        Set(headers, "Permissions-Policy", options.PermissionsPolicy);
        Set(headers, "Cross-Origin-Opener-Policy", options.CrossOriginOpenerPolicy);
        Set(headers, "Cross-Origin-Resource-Policy", options.CrossOriginResourcePolicy);
        if (options.DisableLegacyXssFilter)
            Set(headers, "X-XSS-Protection", "0");

        string? csp = options.ContentSecurityPolicy;
        foreach ((string prefix, string policy) in _cspByPath)
        {
            if (context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                csp = policy;
                break;
            }
        }

        Set(headers, options.ContentSecurityPolicyReportOnly ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy", csp);

        if (options.RemoveServerHeaders)
        {
            headers.Remove("Server");
            headers.Remove("X-Powered-By");
        }
    }

    private static void Set(IHeaderDictionary headers, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !headers.ContainsKey(name))
            headers[name] = value; // uç noktanın kendi verdiği başlık ezilmez
    }
}

public static class SecurityHeadersExtensions
{
    /// <summary>
    /// Güvenlik başlıkları (HSTS, CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy,
    /// Cross-Origin-*). <b>Varsayılan kapalıdır</b>; bu çağrı açar. Varsayılanlar JSON dönen bir API içindir; aynı
    /// sunucudan bir SPA veriliyorsa CSP'yi ona göre ayarla (<see cref="SecurityHeadersOptions.ContentSecurityPolicyByPath"/>).
    /// </summary>
    public static IServiceCollection AddCanSecurityHeaders(this IServiceCollection services, Action<SecurityHeadersOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new SecurityHeadersOptions();
        configure?.Invoke(options);
        services.RemoveAll<SecurityHeadersOptions>();
        services.AddSingleton(options);
        if (options.RemoveServerHeaders)
            services.Configure<KestrelServerOptions>(kestrel => kestrel.AddServerHeader = false);
        return services;
    }

    /// <summary>Pipeline'ın başına yakın ekle (hata yanıtları da başlık alsın): <c>UseCanExceptionHandler()</c>'dan önce.</summary>
    public static IApplicationBuilder UseCanSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        StartupValidation.Require<SecurityHeadersOptions>(app, "UseCanSecurityHeaders", "builder.Services.AddCanSecurityHeaders()");
        return app.UseMiddleware<SecurityHeadersMiddleware>();
    }
}
