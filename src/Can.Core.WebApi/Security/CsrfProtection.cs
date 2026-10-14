using System.Security.Cryptography;
using System.Text;
using Can.Core.Security.Events;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.WebApi.Security;

/// <summary>Çift gönderimli (double-submit) CSRF token'ının ne zaman isteneceği.</summary>
public enum CsrfTokenMode
{
    /// <summary>Auth cookie'si <c>SameSite=Strict</c> değilse (ör. ön yüz farklı sitede, <c>None</c>) istenir.</summary>
    Auto = 0,

    /// <summary>Her zaman istenir.</summary>
    Always = 1,

    /// <summary>Hiç istenmez; yalnızca origin denetimi yapılır.</summary>
    Never = 2,
}

/// <summary><c>AddCanCsrfProtection(...)</c> ayarları.</summary>
public sealed class CsrfOptions
{
    /// <summary>Token'ın ne zaman isteneceği. Origin denetimi her zaman yapılır.</summary>
    public CsrfTokenMode TokenMode { get; set; } = CsrfTokenMode.Auto;

    /// <summary>
    /// Token cookie'si. JavaScript okuyabilsin diye HttpOnly DEĞİLDİR (içinde yetki yoktur; tek başına işe yaramaz).
    /// Axios ve Angular bu adı kendiliğinden kullanır.
    /// </summary>
    public string CookieName { get; set; } = "XSRF-TOKEN";

    /// <summary>İstemcinin token'ı geri gönderdiği başlık (Axios/Angular varsayılanı).</summary>
    public string HeaderName { get; set; } = "X-XSRF-TOKEN";

    /// <summary>Başka siteden gelse de güvenilen origin'ler (<c>AddCanCors</c>'daki origin'ler kendiliğinden güvenilir).</summary>
    public IList<string> TrustedOrigins { get; } = [];

    /// <summary>Denetlenmeyen yollar (ön ek); ör. dış servislerin çağırdığı webhook'lar. Uç noktada: <c>.DisableCsrfProtection()</c>.</summary>
    public IList<string> ExemptPaths { get; } = [];

    /// <summary>
    /// Tarayıcı olmayan istemciler (mobil uygulama, servis, curl: ne <c>Origin</c> ne <c>Sec-Fetch-Site</c> gönderir)
    /// da token göndermek zorunda. Varsayılan kapalı: CSRF yalnızca tarayıcıda yapılabilen bir saldırıdır.
    /// </summary>
    public bool RequireTokenForNonBrowserClients { get; set; }
}

/// <summary>Uç nokta CSRF denetiminden muaf (dış servislerin çağırdığı webhook'lar).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class DisableCsrfProtectionAttribute : Attribute;

/// <summary>
/// İki katmanlı CSRF koruması (OWASP):
/// <list type="number">
/// <item><b>Origin denetimi:</b> veri değiştiren isteklerde (POST/PUT/PATCH/DELETE) tarayıcının değiştirilemeyen
/// <c>Sec-Fetch-Site</c> başlığı <c>cross-site</c>/<c>same-site</c> ise origin güvenilir listede olmalı; başlık yoksa
/// (eski tarayıcı) <c>Origin</c> aynı origin ya da güvenilir olmalı.</item>
/// <item><b>Çift gönderimli token:</b> sunucu imzalı bir token'ı cookie'ye yazar, istemci aynı değeri başlıkta geri
/// gönderir. Kötü site cookie'yi okuyamaz (Same-Origin Policy), başlık da ekleyemez (CORS).</item>
/// </list>
/// </summary>
internal sealed partial class CsrfMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CsrfOptions _options;
    private readonly AuthCookieOptions? _cookies;
    private readonly CanCorsOptions? _cors;
    private readonly IHostEnvironment _environment;
    private readonly IDataProtector _protector;
    private readonly ILogger<CsrfMiddleware> _logger;
    private readonly bool _tokenRequired;

    public CsrfMiddleware(RequestDelegate next, CsrfOptions options, IHostEnvironment environment, IDataProtectionProvider dataProtection, IServiceProvider services, ILogger<CsrfMiddleware> logger)
    {
        _next = next;
        _options = options;
        _environment = environment;
        _logger = logger;
        _cookies = services.GetService<AuthCookieOptions>();
        _cors = services.GetService<CanCorsOptions>();
        _protector = dataProtection.CreateProtector("Can.Core.WebApi.Csrf.v1");
        _tokenRequired = options.TokenMode switch
        {
            CsrfTokenMode.Always => true,
            CsrfTokenMode.Never => false,
            _ => _cookies is not null && _cookies.SameSite != SameSiteMode.Strict,
        };
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExempt(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? cookieToken = context.Request.Cookies[_options.CookieName];
        bool cookieValid = _tokenRequired && IsValidToken(cookieToken);

        if (!IsSafe(context.Request.Method))
        {
            string? origin = context.Request.Headers.Origin.ToString() is { Length: > 0 } o ? o : null;
            string? site = context.Request.Headers["Sec-Fetch-Site"].ToString() is { Length: > 0 } s ? s : null;
            bool browser = origin is not null || site is not null;

            if (OriginRejection(context, site, origin) is { } reason)
            {
                await RejectAsync(context, "csrf_rejected", reason, "İstek başka bir siteden geldiği için reddedildi.").ConfigureAwait(false);
                return;
            }

            if (_tokenRequired && (browser || _options.RequireTokenForNonBrowserClients))
            {
                string header = context.Request.Headers[_options.HeaderName].ToString();
                if (!cookieValid || header.Length == 0 || !FixedTimeEquals(header, cookieToken!))
                {
                    await RejectAsync(context, "csrf_token_invalid", "token", $"'{_options.HeaderName}' başlığı eksik ya da '{_options.CookieName}' cookie'siyle eşleşmiyor.").ConfigureAwait(false);
                    return;
                }
            }
        }

        if (_tokenRequired && !cookieValid)
            IssueToken(context);

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>Bu istekte üretilen token (<see cref="CsrfProtectionExtensions.GetCsrfToken"/>).</summary>
    internal const string TokenItem = "Can.Csrf.Token";

    private string IssueToken(HttpContext context)
    {
        string token = _protector.Protect(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        context.Items[TokenItem] = token;
        context.Response.Cookies.Append(_options.CookieName, token, new CookieOptions
        {
            HttpOnly = false, // istemci okuyup başlığa koyacak
            Secure = _cookies?.Secure ?? context.Request.IsHttps,
            SameSite = _cookies?.SameSite ?? SameSiteMode.Strict,
            Domain = _cookies?.Domain,
            Path = "/",
            IsEssential = true,
        });
        return token;
    }

    private bool IsValidToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 1024)
            return false;
        try
        {
            _protector.Unprotect(token); // sunucunun ürettiği (imzalı) token mı
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private string? OriginRejection(HttpContext context, string? site, string? origin)
    {
        if (site is not null)
        {
            // Tarayıcının hesapladığı, JavaScript'in değiştiremediği başlık
            if (site is "same-origin" or "none")
                return null;
            return origin is not null && IsTrusted(origin) ? null : site; // cross-site, same-site (alt alan adları dahil)
        }

        if (origin is null)
            return null; // tarayıcı değil (mobil, servis) ya da çok eski tarayıcı
        if (origin == "null")
            return "null_origin"; // sandbox'lı iframe, file://
        string self = $"{context.Request.Scheme}://{context.Request.Host}";
        return string.Equals(origin, self, StringComparison.OrdinalIgnoreCase) || IsTrusted(origin) ? null : "origin";
    }

    private bool IsTrusted(string origin) =>
        _options.TrustedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
        || (_cors is not null && CanCorsExtensions.IsAllowed(_cors, _environment, origin));

    private bool IsExempt(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<DisableCsrfProtectionAttribute>() is not null)
            return true;
        foreach (string path in _options.ExemptPaths)
        {
            if (context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private async Task RejectAsync(HttpContext context, string code, string reason, string detail)
    {
        LogRejected(context.Request.Method, context.Request.Path, reason);
        if (context.RequestServices.GetService<ISecurityEvents>() is { } events)
            await events.PublishAsync(new SecurityEvent(SecurityEventTypes.CsrfRejected) { Reason = reason }, context.RequestAborted).ConfigureAwait(false);

        var problem = new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = ProblemTitles.Forbidden, Detail = detail };
        problem.Extensions["code"] = code;
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        IProblemDetailsService? problems = context.RequestServices.GetService<IProblemDetailsService>();
        if (problems is null || !await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }).ConfigureAwait(false))
            await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "CSRF: {Method} {Path} reddedildi ({Reason}).")]
    private partial void LogRejected(string method, PathString path, string reason);
}

/// <summary>
/// Auth cookie'si <c>SameSite=Strict</c> değil ama CSRF koruması açılmamışsa açılışta uyarır (uygulamayı durdurmaz).
/// </summary>
internal sealed partial class CsrfAdvisor(IServiceProvider services, ILogger<CsrfAdvisor> logger) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (services.GetService<AuthCookieOptions>() is { SameSite: not SameSiteMode.Strict } cookies && services.GetService<CsrfOptions>() is null)
            LogUnprotected(logger, cookies.SameSite);
        return next;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth cookie'si SameSite={SameSite}: başka sitelerden gelen isteklerle de gönderilir ve CSRF koruması kapalı. builder.Services.AddCanCsrfProtection() + app.UseCanCsrfProtection() ile aç.")]
    private static partial void LogUnprotected(ILogger logger, SameSiteMode sameSite);
}

public static class CsrfProtectionExtensions
{
    /// <summary>
    /// CSRF koruması (<b>varsayılan kapalıdır</b>; bu çağrı açar). Token cookie'de taşındığı için tarayıcı onu başka
    /// siteden gelen isteklere de ekleyebilir; bu koruma o istekleri reddeder. <see cref="CsrfOptions"/>.
    /// </summary>
    public static IServiceCollection AddCanCsrfProtection(this IServiceCollection services, Action<CsrfOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new CsrfOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.CookieName) || string.IsNullOrWhiteSpace(options.HeaderName))
            throw new InvalidOperationException("CsrfOptions: CookieName ve HeaderName boş olamaz.");

        services.RemoveAll<CsrfOptions>();
        services.AddSingleton(options);
        services.AddDataProtection();
        return services;
    }

    /// <summary>
    /// <c>UseAuthentication()</c>'dan önce ya da sonra; uç nokta muafiyetlerinin (<c>.DisableCsrfProtection()</c>) görülmesi
    /// için routing'den (<c>UseCanApiVersioning</c>) SONRA ekle.
    /// </summary>
    public static IApplicationBuilder UseCanCsrfProtection(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        StartupValidation.Require<CsrfOptions>(app, "UseCanCsrfProtection", "builder.Services.AddCanCsrfProtection()");
        return app.UseMiddleware<CsrfMiddleware>();
    }

    /// <summary>
    /// İstemcinin göndermesi gereken token: bu istekte üretildiyse o, değilse cookie'deki. Cookie okuyamayan istemciye
    /// gövdede vermek için (ör. giriş yanıtında).
    /// </summary>
    public static string? GetCsrfToken(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(CsrfMiddleware.TokenItem, out object? issued) && issued is string token)
            return token;
        CsrfOptions? options = context.RequestServices.GetService<CsrfOptions>();
        return options is null ? null : context.Request.Cookies[options.CookieName];
    }

    /// <summary>Uç noktayı (ya da grubu) CSRF denetiminden muaf tutar (dış servislerin çağırdığı webhook'lar).</summary>
    public static TBuilder DisableCsrfProtection<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new DisableCsrfProtectionAttribute());
    }
}
