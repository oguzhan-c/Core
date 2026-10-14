using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Versioning;

/// <summary>Bir API sürümü ve yaşam döngüsü.</summary>
/// <param name="Major">Sürüm numarası (<c>/api/v2</c>).</param>
public sealed record ApiVersionInfo(int Major)
{
    /// <summary>Kullanımdan kalktığı an: yanıtlara <c>Deprecation</c> başlığı eklenir (RFC 9745).</summary>
    public DateTimeOffset? DeprecatedAt { get; init; }

    /// <summary>Kapanacağı an: yanıtlara <c>Sunset</c> başlığı eklenir (RFC 8594); bu andan sonra 410 döner.</summary>
    public DateTimeOffset? Sunset { get; init; }

    /// <summary>Geçiş rehberi (<c>Link: &lt;...&gt;; rel="deprecation"</c>).</summary>
    public string? Link { get; init; }
}

/// <summary><c>AddCanApiVersioning(...)</c> ayarları.</summary>
public sealed class CanApiVersioningOptions
{
    private readonly List<ApiVersionInfo> _versions = [];

    /// <summary>Sürümlenen yolların ön eki.</summary>
    public string PathPrefix { get; set; } = "/api";

    /// <summary>Yolda sürüm yoksa bu başlıktan okunur (<c>api-version: 2</c>).</summary>
    public string HeaderName { get; set; } = "api-version";

    /// <summary>
    /// Ne yolda ne başlıkta sürüm yoksa kullanılan sürüm. Boşsa sürümsüz istekler olduğu gibi geçer (sürümlü uç
    /// noktalara ulaşmaz).
    /// </summary>
    public int? DefaultVersion { get; set; }

    /// <summary>
    /// Ön ekin altında olup sürümlenmeyen yollar (ör. <c>/api/auth</c>, <c>/api/features</c>): bunlara başlık ya da
    /// varsayılan sürüm uygulanmaz.
    /// </summary>
    public IList<string> UnversionedPaths { get; } = [];

    public IReadOnlyList<ApiVersionInfo> Versions => _versions;

    /// <summary>Sürüm ekler.</summary>
    /// <example><code>o.Add(new ApiVersionInfo(1) { DeprecatedAt = ..., Sunset = ..., Link = "https://docs/v2-gecis" }).Add(new(2));</code></example>
    public CanApiVersioningOptions Add(ApiVersionInfo version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Major < 1 || _versions.Any(v => v.Major == version.Major))
            throw new ArgumentException($"Sürüm {version.Major} geçersiz ya da zaten var.", nameof(version));
        _versions.Add(version);
        _versions.Sort((a, b) => a.Major.CompareTo(b.Major));
        return this;
    }

    internal ApiVersionInfo? Find(int major) => _versions.FirstOrDefault(v => v.Major == major);
}

public static class ApiVersioningExtensions
{
    /// <summary>
    /// URL (<c>/api/v2/orders</c>) ya da başlık (<c>api-version: 2</c>) ile sürümleme. Ek paket yok.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanApiVersioning(o =&gt;
    /// {
    ///     o.Add(new ApiVersionInfo(1) { DeprecatedAt = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), Sunset = new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero) });
    ///     o.Add(new ApiVersionInfo(2));
    ///     o.DefaultVersion = 2;
    /// });
    ///
    /// app.UseCanExceptionHandler();
    /// app.UseCanApiVersioning();          // routing'den önce (UseRouting'i kendisi çağırır)
    /// app.UseAuthentication(); ...
    ///
    /// app.MapApiVersion(1).MapGet("/orders", GetOrdersV1);   // /api/v1/orders
    /// app.MapApiVersion(2).MapGet("/orders", GetOrdersV2);   // /api/v2/orders
    /// </code>
    /// </example>
    public static IServiceCollection AddCanApiVersioning(this IServiceCollection services, Action<CanApiVersioningOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new CanApiVersioningOptions();
        configure(options);
        if (options.Versions.Count == 0)
            throw new InvalidOperationException("En az bir API sürümü tanımlanmalı.");
        if (options.DefaultVersion is { } fallback && options.Find(fallback) is null)
            throw new InvalidOperationException($"Varsayılan sürüm {fallback} tanımlı değil.");
        options.PathPrefix = "/" + options.PathPrefix.Trim('/');
        services.AddSingleton(options);
        return services;
    }

    /// <summary>
    /// Sürümü belirler (gerekirse yolu <c>/api/v{n}</c>'ye yeniden yazar) ve ardından <c>UseRouting()</c> çağırır.
    /// <c>UseCanExceptionHandler</c>'dan sonra, kimlik doğrulamadan önce ekle.
    /// </summary>
    public static IApplicationBuilder UseCanApiVersioning(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        DependencyInjection.StartupValidation.Require<CanApiVersioningOptions>(app, "UseCanApiVersioning", "builder.Services.AddCanApiVersioning(o => o.Add(new ApiVersionInfo(1)))");
        app.UseMiddleware<ApiVersionMiddleware>();
        return app.UseRouting();
    }

    /// <summary>Sürümün uç nokta grubu: <c>{PathPrefix}/v{major}</c> (OpenAPI belge grubu <c>v{major}</c>).</summary>
    public static RouteGroupBuilder MapApiVersion(this IEndpointRouteBuilder endpoints, int major)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        CanApiVersioningOptions options = endpoints.ServiceProvider.GetRequiredService<CanApiVersioningOptions>();
        if (options.Find(major) is null)
            throw new InvalidOperationException($"API sürümü {major} AddCanApiVersioning'de tanımlı değil.");
        string group = "v" + major.ToString(CultureInfo.InvariantCulture);
        return endpoints.MapGroup($"{options.PathPrefix}/{group}").WithGroupName(group);
    }

    /// <summary>İsteğin API sürümü (<see cref="ApiVersionMiddleware"/> belirledi; sürümsüz istekte <see langword="null"/>).</summary>
    public static ApiVersionInfo? GetApiVersion(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<ApiVersionFeature>()?.Version;
    }
}

internal sealed record ApiVersionFeature(ApiVersionInfo Version);

/// <summary>
/// Sürümü yoldan, yoksa başlıktan, yoksa varsayılandan alır. Bilinmeyen sürüm 400 (<c>api_version_unsupported</c>),
/// kapanmış sürüm 410 (<c>api_version_retired</c>). Yanıtlara <c>api-supported-versions</c>,
/// <c>api-deprecated-versions</c> ve kullanımdan kalkan sürümde <c>Deprecation</c> / <c>Sunset</c> / <c>Link</c> eklenir.
/// </summary>
internal sealed class ApiVersionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CanApiVersioningOptions _options;
    private readonly TimeProvider _time;
    private readonly string _supported;
    private readonly string? _deprecated;

    public ApiVersionMiddleware(RequestDelegate next, CanApiVersioningOptions options, TimeProvider? timeProvider = null)
    {
        _next = next;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _supported = string.Join(", ", options.Versions.Select(v => v.Major.ToString(CultureInfo.InvariantCulture)));
        string deprecated = string.Join(", ", options.Versions.Where(v => v.DeprecatedAt is not null).Select(v => v.Major.ToString(CultureInfo.InvariantCulture)));
        _deprecated = deprecated.Length > 0 ? deprecated : null;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(_options.PathPrefix, StringComparison.OrdinalIgnoreCase, out PathString rest))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (_options.UnversionedPaths.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        int? requested;
        bool inPath = TryReadSegment(rest, out int fromPath);
        if (inPath)
        {
            requested = fromPath;
        }
        else if (context.Request.Headers[_options.HeaderName] is { Count: > 0 } header)
        {
            if (!TryParse(header.ToString(), out int fromHeader))
            {
                await ProblemAsync(context, StatusCodes.Status400BadRequest, ProblemTitles.Validation, "api_version_unsupported", $"'{header}' geçerli bir API sürümü değil. Desteklenenler: {_supported}.").ConfigureAwait(false);
                return;
            }

            requested = fromHeader;
        }
        else
        {
            requested = _options.DefaultVersion;
        }

        if (requested is null)
        {
            await _next(context).ConfigureAwait(false); // sürümsüz uç nokta (ör. /api/auth)
            return;
        }

        ApiVersionInfo? version = _options.Find(requested.Value);
        AddHeaders(context, version);
        if (version is null)
        {
            await ProblemAsync(context, StatusCodes.Status400BadRequest, ProblemTitles.Validation, "api_version_unsupported", $"API sürümü {requested} desteklenmiyor. Desteklenenler: {_supported}.").ConfigureAwait(false);
            return;
        }

        if (version.Sunset <= _time.GetUtcNow())
        {
            await ProblemAsync(context, StatusCodes.Status410Gone, ProblemTitles.Gone, "api_version_retired", $"API sürümü {version.Major} kapatıldı. Desteklenenler: {_supported}.").ConfigureAwait(false);
            return;
        }

        context.Features.Set(new ApiVersionFeature(version));
        if (!inPath)
        {
            // /api/orders + api-version: 2 → /api/v2/orders (yönlendirme değil, iç yeniden yazım)
            context.Request.Path = _options.PathPrefix + "/v" + version.Major.ToString(CultureInfo.InvariantCulture) + rest;
        }

        await _next(context).ConfigureAwait(false);
    }

    private void AddHeaders(HttpContext context, ApiVersionInfo? version)
    {
        IHeaderDictionary headers = context.Response.Headers;
        headers["api-supported-versions"] = _supported;
        if (_deprecated is not null)
            headers["api-deprecated-versions"] = _deprecated;
        if (version?.DeprecatedAt is { } deprecatedAt)
            headers["Deprecation"] = "@" + deprecatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        if (version?.Sunset is { } sunset)
            headers["Sunset"] = sunset.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        if (version?.Link is { Length: > 0 } link && version.DeprecatedAt is not null)
            headers.Append("Link", $"<{link}>; rel=\"deprecation\"");
    }

    /// <summary><c>/v2</c>, <c>/v2/...</c> → 2.</summary>
    private static bool TryReadSegment(PathString rest, out int major)
    {
        major = 0;
        string? value = rest.Value;
        if (value is not { Length: > 2 } || value[0] != '/' || (value[1] != 'v' && value[1] != 'V'))
            return false;
        int end = value.IndexOf('/', 1);
        return TryParse(end < 0 ? value[2..] : value[2..end], out major);
    }

    /// <summary><c>2</c>, <c>2.0</c> → 2.</summary>
    private static bool TryParse(string text, out int major)
    {
        string head = text.Trim();
        int dot = head.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            if (!int.TryParse(head.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int minor) || minor != 0)
            {
                major = 0;
                return false;
            }

            head = head[..dot];
        }

        return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out major) && major > 0;
    }

    private static async Task ProblemAsync(HttpContext context, int status, string title, string code, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        context.Response.StatusCode = status;
        IProblemDetailsService? problemDetails = context.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetails is null || !await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }).ConfigureAwait(false))
            await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
    }
}
