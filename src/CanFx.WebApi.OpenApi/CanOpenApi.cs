using System.Globalization;
using System.Net;
using System.Text.Json;
using CanFx.WebApi.DependencyInjection;
using CanFx.WebApi.Idempotency;
using CanFx.WebApi.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;

namespace CanFx.WebApi.OpenApi;

/// <summary><c>AddCanOpenApi(...)</c> ayarları.</summary>
public sealed class CanOpenApiOptions
{
    /// <summary>Belge başlığı.</summary>
    public string Title { get; set; } = "API";

    /// <summary>Belge açıklaması (Markdown).</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Belge adları. Boşsa: API sürümleme açıksa her sürüm için bir belge (<c>v1</c>, <c>v2</c>), değilse <c>v1</c>.
    /// Sürümsüz uç noktalar her belgede görünür.
    /// </summary>
    public IList<string> Documents { get; } = [];

    /// <summary>
    /// Production'da da yayınla. Varsayılan kapalı: API'nin tüm yüzeyini (uç noktalar, alanlar, hata kodları) dışarıya
    /// göstermemek için belge ve arayüz yalnızca Development'ta açılır.
    /// </summary>
    public bool ExposeInProduction { get; set; }

    /// <summary>Belge adresi; <c>{documentName}</c> yer tutucusunu içermeli.</summary>
    public string DocumentPath { get; set; } = "/openapi/{documentName}.json";

    /// <summary>Etkileşimli arayüzün (Scalar) adresi; boşsa arayüz yayınlanmaz.</summary>
    public string? ReferencePath { get; set; } = "/docs";

    /// <summary>
    /// Arayüzün betiği. Paket eklenmez; tarayıcı CDN'den yükler. İnternete kapalı ortamda betiği kendi sunucundan ver
    /// (<c>wwwroot</c>) ve adresini buraya yaz.
    /// </summary>
    public string ReferenceScriptUrl { get; set; } = "https://cdn.jsdelivr.net/npm/@scalar/api-reference@1";

    /// <summary>
    /// Uç noktalara ortak ProblemDetails hata yanıtları eklenir (400 doğrulama, 401, 403, 500; idempotent uç noktalarda
    /// 409 ve 422). Uç noktanın kendi bildirdiği yanıtlar korunur.
    /// </summary>
    public bool DescribeProblemResponses { get; set; } = true;
}

public static class CanOpenApiExtensions
{
    /// <summary>Cookie'deki access token'ın güvenlik şeması adı.</summary>
    public const string CookieScheme = "cookieAuth";

    /// <summary><c>Authorization: Bearer</c> güvenlik şeması adı (<c>AllowAuthorizationHeader</c> açıksa).</summary>
    public const string BearerScheme = "bearerAuth";

    /// <summary>
    /// OpenAPI belgeleri: API sürümü başına belge, kullanımdan kalkan sürümlerin uç noktaları <c>deprecated</c>, cookie
    /// (ve izinliyse Bearer) güvenlik şeması, yetki isteyen uç noktalarda güvenlik gereksinimi, ProblemDetails hata
    /// yanıtları ve <c>Idempotency-Key</c> başlığı. <c>AddCanApiVersioning</c>'den SONRA çağır.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanApiVersioning(o =&gt; { o.Add(new(1) { DeprecatedAt = ... }); o.Add(new(2)); });
    /// builder.Services.AddCanOpenApi(o =&gt; o.Title = "Mağaza API");
    ///
    /// app.MapCanOpenApi();   // /openapi/v1.json, /openapi/v2.json, /docs (yalnızca Development)
    /// </code>
    /// </example>
    public static IServiceCollection AddCanOpenApi(this IServiceCollection services, Action<CanOpenApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new CanOpenApiOptions();
        configure?.Invoke(options);
        if (!options.DocumentPath.Contains("{documentName}", StringComparison.Ordinal))
            throw new InvalidOperationException("CanOpenApiOptions.DocumentPath '{documentName}' içermeli.");

        if (options.Documents.Count == 0)
        {
            CanApiVersioningOptions? versioning = services
                .Where(d => d.ServiceType == typeof(CanApiVersioningOptions))
                .Select(d => d.ImplementationInstance as CanApiVersioningOptions)
                .LastOrDefault(o => o is not null);
            foreach (string name in versioning?.Versions.Select(v => DocumentName(v.Major)) ?? new[] { "v1" })
                options.Documents.Add(name);
        }

        services.AddSingleton(options);
        foreach (string document in options.Documents.Distinct(StringComparer.Ordinal))
        {
            services.AddOpenApi(document, o =>
            {
                o.AddDocumentTransformer<CanDocumentTransformer>();
                o.AddOperationTransformer<CanOperationTransformer>();
            });
        }

        return services;
    }

    /// <summary>
    /// Belgeleri (<see cref="CanOpenApiOptions.DocumentPath"/>) ve arayüzü (<see cref="CanOpenApiOptions.ReferencePath"/>)
    /// yayınlar. Production'da <see cref="CanOpenApiOptions.ExposeInProduction"/> açık değilse hiçbir şey yayınlanmaz.
    /// </summary>
    public static IEndpointRouteBuilder MapCanOpenApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        CanOpenApiOptions options = endpoints.ServiceProvider.GetService<CanOpenApiOptions>()
            ?? throw new InvalidOperationException("MapCanOpenApi için önce builder.Services.AddCanOpenApi() çağır.");
        IHostEnvironment environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment() && !options.ExposeInProduction)
            return endpoints;

        endpoints.MapOpenApi(options.DocumentPath);

        if (!string.IsNullOrWhiteSpace(options.ReferencePath))
        {
            string html = ReferencePage(options);
            endpoints.MapGet(options.ReferencePath, () => Results.Content(html, "text/html; charset=utf-8")).ExcludeFromDescription();
        }

        return endpoints;
    }

    internal static string DocumentName(int major) => "v" + major.ToString(CultureInfo.InvariantCulture);

    /// <summary>Scalar arayüzü: tüm belgeler seçilebilir; istekler aynı origin'den cookie ile gider.</summary>
    private static string ReferencePage(CanOpenApiOptions options)
    {
        var sources = options.Documents.Distinct(StringComparer.Ordinal)
            .Select(d => new { title = d, url = options.DocumentPath.Replace("{documentName}", d, StringComparison.Ordinal) })
            .ToArray();
        string config = JsonSerializer.Serialize(new { sources })
            .Replace("<", "\\u003c", StringComparison.Ordinal); // </script> ile betikten çıkılamasın

        return $$"""
            <!doctype html>
            <html lang="tr">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>{{WebUtility.HtmlEncode(options.Title)}}</title>
            </head>
            <body>
              <div id="app"></div>
              <script src="{{WebUtility.HtmlEncode(options.ReferenceScriptUrl)}}"></script>
              <script>Scalar.createApiReference('#app', {{config}});</script>
            </body>
            </html>
            """;
    }
}

/// <summary>Başlık, sürüm açıklaması ve güvenlik şemaları.</summary>
internal sealed class CanDocumentTransformer(CanOpenApiOptions options, IServiceProvider services) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info ??= new OpenApiInfo();
        document.Info.Title = options.Title;
        document.Info.Version = context.DocumentName;

        string? description = options.Description;
        if (CanOpenApiVersions.Find(services, context.DocumentName) is { DeprecatedAt: not null } version)
        {
            string notice = version.Sunset is { } sunset
                ? $"**Bu sürüm kullanımdan kalktı** ve {sunset:yyyy-MM-dd} tarihinde kapanacak."
                : "**Bu sürüm kullanımdan kalktı.**";
            if (version.Link is { } link)
                notice += $" Geçiş: {link}";
            description = string.IsNullOrWhiteSpace(description) ? notice : notice + "\n\n" + description;
        }

        document.Info.Description = description;

        AuthCookieOptions cookies = services.GetService<AuthCookieOptions>() ?? new AuthCookieOptions();
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[CanOpenApiExtensions.CookieScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = cookies.AccessTokenCookieName,
            Description = "Access token HttpOnly cookie'de taşınır; giriş uç noktası cookie'yi yazar, tarayıcı her istekte gönderir.",
        };
        if (cookies.AllowAuthorizationHeader)
        {
            document.Components.SecuritySchemes[CanOpenApiExtensions.BearerScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Mobil ve servis istemcileri için Authorization: Bearer başlığı.",
            };
        }

        return Task.CompletedTask;
    }
}

/// <summary>Güvenlik gereksinimi, hata yanıtları, idempotency başlığı ve kullanımdan kalkma.</summary>
internal sealed class CanOperationTransformer(CanOpenApiOptions options, IServiceProvider services) : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;
        bool requiresAuth = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
        bool hasPolicy = metadata.OfType<IAuthorizeData>().Any(a => !string.IsNullOrEmpty(a.Policy) || !string.IsNullOrEmpty(a.Roles))
            || metadata.OfType<AuthorizationPolicy>().Any();
        IdempotentAttribute? idempotent = metadata.OfType<IdempotentAttribute>().LastOrDefault();

        if (CanOpenApiVersions.Find(services, context.DocumentName) is { DeprecatedAt: not null })
            operation.Deprecated = true;

        if (requiresAuth)
        {
            AuthCookieOptions cookies = services.GetService<AuthCookieOptions>() ?? new AuthCookieOptions();
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(CanOpenApiExtensions.CookieScheme, context.Document)] = [] });
            if (cookies.AllowAuthorizationHeader)
                operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(CanOpenApiExtensions.BearerScheme, context.Document)] = [] });
        }

        if (idempotent is not null)
        {
            string header = services.GetService<CanIdempotencyOptions>()?.HeaderName ?? "Idempotency-Key";
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = header,
                In = ParameterLocation.Header,
                Required = idempotent.Required,
                Description = "Tekrar denemede aynı değer gönderilirse istek ikinci kez işlenmez, ilk yanıt döner (ör. bir UUID).",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = 255 },
            });
        }

        if (!options.DescribeProblemResponses)
            return;

        IOpenApiSchema problem = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, cancellationToken).ConfigureAwait(false);
        operation.Responses ??= new OpenApiResponses();

        if (context.Description.ParameterDescriptions.Count > 0)
        {
            IOpenApiSchema validation = await context.GetOrCreateSchemaAsync(typeof(HttpValidationProblemDetails), null, cancellationToken).ConfigureAwait(false);
            Add(operation, "400", "Doğrulama hatası (alan bazında errors) ya da iş kuralı ihlali.", validation);
        }

        if (requiresAuth)
            Add(operation, "401", "Giriş gerekli ya da oturum sonlandırıldı (code: session_revoked).", problem);
        if (hasPolicy)
            Add(operation, "403", "Yetki yok.", problem);
        if (idempotent is not null)
        {
            Add(operation, "409", "Aynı anahtarla gönderilen istek hâlâ işleniyor (code: idempotency_in_progress).", problem);
            Add(operation, "422", "Anahtar farklı bir istekle kullanılmış (code: idempotency_key_reused).", problem);
        }

        Add(operation, "500", "Sunucu hatası.", problem);
    }

    private static void Add(OpenApiOperation operation, string status, string description, IOpenApiSchema schema)
    {
        if (operation.Responses!.ContainsKey(status))
            return; // uç noktanın kendi bildirdiği yanıt korunur

        operation.Responses[status] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType { Schema = schema },
            },
        };
    }
}

internal static class CanOpenApiVersions
{
    public static ApiVersionInfo? Find(IServiceProvider services, string documentName) =>
        services.GetService<CanApiVersioningOptions>()?.Versions.FirstOrDefault(v => CanOpenApiExtensions.DocumentName(v.Major) == documentName);
}
