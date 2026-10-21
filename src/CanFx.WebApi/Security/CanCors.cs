using CanFx.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CanFx.WebApi.Security;

/// <summary><c>AddCanCors(...)</c> ayarları.</summary>
public sealed class CanCorsOptions
{
    /// <summary>Politika adı.</summary>
    public const string PolicyName = "can";

    /// <summary>
    /// İzin verilen origin'ler, tam haliyle (<c>https://app.example.com</c>; yol ve sonda <c>/</c> yok). Kimlik bilgisiyle
    /// (cookie) kullanıldığı için <c>*</c> verilemez.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = [];

    /// <summary>
    /// Development ortamında <c>http(s)://localhost</c> ve <c>127.0.0.1</c>'in her portu izinli (Vite, React dev server).
    /// Production'da etkisizdir.
    /// </summary>
    public bool AllowLocalhostInDevelopment { get; set; } = true;

    /// <summary>
    /// Cookie'ler (access token) farklı origin'den gelen istekle gönderilsin. Ön yüz <c>fetch(..., { credentials: "include" })</c>
    /// kullanmalı. Varsayılan açık: token cookie'de taşındığı için kapalıyken giriş çalışmaz.
    /// </summary>
    public bool AllowCredentials { get; set; } = true;

    /// <summary>İzin verilen istek başlıkları (ek olarak <c>Content-Type</c>, <c>Accept</c> gibi basit başlıklar her zaman izinli).</summary>
    public IList<string> AllowedHeaders { get; } = ["Content-Type", "Accept", "Accept-Language", "Idempotency-Key", "api-version", "X-XSRF-TOKEN", "If-None-Match", "If-Match"];

    /// <summary>
    /// JavaScript'in okuyabileceği yanıt başlıkları. Tarayıcı bunlar dışındaki özel başlıkları ön yüze göstermez; ETag,
    /// sürüm ve hız sınırı başlıkları bu yüzden listededir.
    /// </summary>
    public IList<string> ExposedHeaders { get; } = ["ETag", "Location", "Retry-After", "Deprecation", "Sunset", "Link", "api-version", "Idempotent-Replayed", "Content-Disposition"];

    /// <summary>İzin verilen yöntemler.</summary>
    public IList<string> AllowedMethods { get; } = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// Tarayıcı ön kontrol (OPTIONS) yanıtını bu süre önbelleğe alır; her POST'tan önce ayrı bir istek atılmaz.
    /// </summary>
    public TimeSpan PreflightMaxAge { get; set; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        var errors = new List<string>();
        foreach (string origin in AllowedOrigins)
        {
            if (origin == "*")
            {
                if (AllowCredentials)
                    errors.Add("AllowCredentials açıkken \"*\" kullanılamaz: herhangi bir site kullanıcının oturumuyla API'yi çağırıp yanıtı okuyabilirdi. Origin'leri tek tek yaz.");
                continue;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http")
                || !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"'{origin}' geçerli bir origin değil (ör. https://app.example.com; yol ve sonda '/' olmadan).");
            }
        }

        if (PreflightMaxAge < TimeSpan.Zero)
            errors.Add("PreflightMaxAge negatif olamaz.");
        if (errors.Count > 0)
            throw new InvalidOperationException("CanCorsOptions geçersiz:\n- " + string.Join("\n- ", errors));
    }
}

public static class CanCorsExtensions
{
    /// <summary>
    /// Ön yüz farklı bir origin'deyse (ör. <c>app.example.com</c> → <c>api.example.com</c>) CORS. <b>Varsayılan
    /// kapalıdır</b>; ön yüz ve API tek origin'den (reverse proxy ile <c>/api</c>) sunuluyorsa CORS'a gerek yoktur.
    /// Gelen origin'i olduğu gibi geri yansıtmaz; yalnızca listedekilere izin verir.
    /// </summary>
    public static IServiceCollection AddCanCors(this IServiceCollection services, Action<CanCorsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new CanCorsOptions();
        configure(options);
        options.Validate();

        services.RemoveAll<CanCorsOptions>();
        services.AddSingleton(options);
        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IHostEnvironment>((cors, environment) =>
        {
            bool localhost = options.AllowLocalhostInDevelopment && environment.IsDevelopment();
            var allowed = new HashSet<string>(options.AllowedOrigins, StringComparer.OrdinalIgnoreCase);
            bool any = allowed.Contains("*");

            cors.AddPolicy(CanCorsOptions.PolicyName, policy =>
            {
                policy.SetIsOriginAllowed(origin => any || allowed.Contains(origin) || (localhost && IsLocalhost(origin)))
                    .WithHeaders([.. options.AllowedHeaders])
                    .WithExposedHeaders([.. options.ExposedHeaders])
                    .WithMethods([.. options.AllowedMethods])
                    .SetPreflightMaxAge(options.PreflightMaxAge);
                if (options.AllowCredentials)
                    policy.AllowCredentials();
            });
        });
        return services;
    }

    /// <summary><c>UseRouting()</c>'den (<c>UseCanApiVersioning</c> çağırır) sonra, <c>UseAuthentication()</c>'dan önce ekle.</summary>
    public static IApplicationBuilder UseCanCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        StartupValidation.Require<CanCorsOptions>(app, "UseCanCors", "builder.Services.AddCanCors(o => o.AllowedOrigins.Add(\"https://app.example.com\"))");
        return app.UseCors(CanCorsOptions.PolicyName);
    }

    /// <summary>Origin listede mi (CSRF koruması da kullanır).</summary>
    internal static bool IsAllowed(CanCorsOptions options, IHostEnvironment environment, string origin) =>
        options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
        || (options.AllowLocalhostInDevelopment && environment.IsDevelopment() && IsLocalhost(origin));

    private static bool IsLocalhost(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && (uri.IsLoopback || uri.Host == "localhost");
}
