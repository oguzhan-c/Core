using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Can.Core.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.RateLimiting;

/// <summary>Hazır politika adları (<c>.RequireRateLimiting(CanRateLimitPolicies.Auth)</c>).</summary>
public static class CanRateLimitPolicies
{
    /// <summary>Giriş, kayıt, şifre sıfırlama, kod doğrulama: IP başına sıkı sınır (kaba kuvvet denemelerine karşı).</summary>
    public const string Auth = "can-auth";

    /// <summary>Maliyetli/kötüye kullanılabilir işlemler (SMS, e-posta gönderimi, dışa aktarım): kullanıcı/IP başına çok sıkı.</summary>
    public const string Sensitive = "can-sensitive";
}

/// <summary>Bir sınırın ayarı: <see cref="Window"/> içinde en fazla <see cref="PermitLimit"/> istek.</summary>
public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }

    /// <summary>Kayan pencere parça sayısı (1 = sabit pencere). Kayan pencere sınır anındaki patlamaları yumuşatır.</summary>
    public int SegmentsPerWindow { get; set; } = 1;
}

/// <summary>
/// Sınırları sunucular arasında paylaşan sınırlayıcı (ör. Redis: <c>Can.Core.Redis.ScaleOut</c>). Kayıtlıysa her bölüm
/// için bu kullanılır; değilse sayaçlar sunucunun belleğindedir (her sunucu ayrı sayar).
/// </summary>
public interface IDistributedRateLimiterFactory
{
    /// <param name="partitionKey">Bölüm anahtarı (politika + kullanıcı/IP + tenant).</param>
    /// <param name="rule">Sınır.</param>
    RateLimiter Create(string partitionKey, RateLimitRule rule);
}

public sealed class CanRateLimitOptions
{
    /// <summary>Tüm istekler için: kullanıcı (yoksa IP) + tenant başına.</summary>
    public RateLimitRule Global { get; set; } = new() { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6 };

    /// <summary><see cref="CanRateLimitPolicies.Auth"/>: IP başına.</summary>
    public RateLimitRule Auth { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) };

    /// <summary><see cref="CanRateLimitPolicies.Sensitive"/>: kullanıcı (yoksa IP) başına.</summary>
    public RateLimitRule Sensitive { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) };

    /// <summary>Kapalıysa hiçbir sınır uygulanmaz (ör. yük testi). Politikalar yine tanımlıdır.</summary>
    public bool Enabled { get; set; } = true;
}

public static class CanRateLimitingExtensions
{
    /// <summary>
    /// ASP.NET Core'un yerleşik hız sınırlayıcısını hazır politikalarla kurar (ek paket yok). Sınır aşılınca 429 +
    /// <c>Retry-After</c> + ProblemDetails (<c>code: "rate_limited"</c>) döner.
    /// </summary>
    /// <remarks>
    /// IP, <c>HttpContext.Connection.RemoteIpAddress</c>'ten okunur; uygulama bir proxy/yük dengeleyici arkasındaysa
    /// <c>UseForwardedHeaders</c> doğru yapılandırılmalı, yoksa herkes proxy'nin IP'sini paylaşır.
    /// </remarks>
    public static IServiceCollection AddCanRateLimiting(this IServiceCollection services, Action<CanRateLimitOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CanRateLimitOptions();
        configure?.Invoke(options);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = OnRejectedAsync;

            if (options.Enabled)
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => Partition(c, $"global:{UserOrIp(c)}", options.Global));

            limiter.AddPolicy(CanRateLimitPolicies.Auth, c => options.Enabled ? Partition(c, $"auth:{Ip(c)}", options.Auth) : RateLimitPartition.GetNoLimiter("off"));
            limiter.AddPolicy(CanRateLimitPolicies.Sensitive, c => options.Enabled ? Partition(c, $"sensitive:{UserOrIp(c)}", options.Sensitive) : RateLimitPartition.GetNoLimiter("off"));
        });

        return services;
    }

    /// <summary><c>UseAuthentication()</c> ve <c>UseCanTenantResolution()</c>'dan SONRA ekle (kullanıcı/tenant'a göre bölünsün).</summary>
    public static IApplicationBuilder UseCanRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseRateLimiter();
    }

    private static RateLimitPartition<string> Partition(HttpContext context, string key, RateLimitRule rule)
    {
        string tenant = context.RequestServices.GetService<TenantContext>()?.TenantId ?? "-";
        string partitionKey = $"{key}:{tenant}";

        if (context.RequestServices.GetService<IDistributedRateLimiterFactory>() is { } distributed)
            return RateLimitPartition.Get(partitionKey, k => distributed.Create(k, rule));

        return rule.SegmentsPerWindow > 1
            ? RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = rule.PermitLimit,
                Window = rule.Window,
                SegmentsPerWindow = rule.SegmentsPerWindow,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rule.PermitLimit,
                Window = rule.Window,
                QueueLimit = 0,
            });
    }

    private static string UserOrIp(HttpContext context)
    {
        string? user = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return user is not null ? $"u:{user}" : $"ip:{Ip(context)}";
    }

    private static string Ip(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        HttpContext http = context.HttpContext;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = ProblemTitles.TooManyRequests,
            Detail = "Çok fazla istek gönderildi; biraz sonra tekrar dene.",
        };
        problem.Extensions["code"] = "rate_limited";

        IProblemDetailsService? problemDetails = http.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetails is null || !await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem }).ConfigureAwait(false))
            await http.Response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);
    }
}
