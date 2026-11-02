using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using CanFx.MultiTenancy;
using CanFx.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using System.Collections;
using System.Reflection;

namespace CanFx.WebApi.RateLimiting;

/// <summary>Hazır politika adları (<c>.RequireRateLimiting(CanRateLimitPolicies.Auth)</c>).</summary>
public static class CanRateLimitPolicies
{
    /// <summary>Giriş, kayıt, şifre sıfırlama, kod doğrulama: IP başına sıkı sınır (kaba kuvvet denemelerine karşı).</summary>
    public const string Auth = "can-auth";

    /// <summary>Maliyetli/kötüye kullanılabilir işlemler (SMS, e-posta gönderimi, dışa aktarım): kullanıcı/IP başına çok sıkı.</summary>
    public const string Sensitive = "can-sensitive";
}

/// <summary>Sınırın neye göre sayıldığı (her biri ayrıca tenant'a göre ayrılır).</summary>
public enum RateLimitPartitionBy
{
    /// <summary>Kullanıcı (giriş yapmışsa), yoksa IP adresi.</summary>
    UserOrIp = 0,

    /// <summary>IP adresi (giriş, kayıt gibi kimliksiz uçlar).</summary>
    Ip = 1,

    /// <summary>Tenant'taki herkes tek sayaç (ör. tenant başına dışa aktarım kotası).</summary>
    Tenant = 2,
}

/// <summary>Bir sınırın ayarı: <see cref="Window"/> içinde en fazla <see cref="PermitLimit"/> istek.</summary>
public sealed class RateLimitRule
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }

    /// <summary>Kayan pencere parça sayısı (1 = sabit pencere). Kayan pencere sınır anındaki patlamaları yumuşatır.</summary>
    public int SegmentsPerWindow { get; set; } = 1;

    /// <summary>Neye göre sayılır (varsayılan: kullanıcı, yoksa IP).</summary>
    public RateLimitPartitionBy PartitionBy { get; set; } = RateLimitPartitionBy.UserOrIp;
}

/// <summary>
/// Sınırları sunucular arasında paylaşan sınırlayıcı (ör. Redis: <c>CanFx.Redis.ScaleOut</c>). Kayıtlıysa her bölüm
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
    public RateLimitRule Auth { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), PartitionBy = RateLimitPartitionBy.Ip };

    /// <summary><see cref="CanRateLimitPolicies.Sensitive"/>: kullanıcı (yoksa IP) başına.</summary>
    public RateLimitRule Sensitive { get; set; } = new() { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) };

    /// <summary>
    /// Uygulamanın kendi sınırları: ad → kural. Uca <c>[EnableRateLimiting("reports")]</c> ya da
    /// <c>.RequireRateLimiting("reports")</c> ile bağlanır. Tanımsız bir ad kullanan uç varsa uygulama açılışta durur.
    /// </summary>
    /// <example>appsettings: <c>"Policies": { "reports": { "PermitLimit": 20, "Window": "00:01:00", "PartitionBy": "UserOrIp" } }</c></example>
    public Dictionary<string, RateLimitRule> Policies { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Kapalıysa hiçbir sınır uygulanmaz (ör. yük testi). Politikalar yine tanımlıdır.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Tanımlı politika adları (hazır olanlar + <see cref="Policies"/>).</summary>
    internal IEnumerable<string> PolicyNames => new[] { CanRateLimitPolicies.Auth, CanRateLimitPolicies.Sensitive }.Concat(Policies.Keys);
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
        if (options.Enabled)
        {
            string[] errors = new[] { ("Global", options.Global), ("Auth", options.Auth), ("Sensitive", options.Sensitive) }
                .Concat(options.Policies.Select(p => ($"Policies.{p.Key}", p.Value)))
                .SelectMany(r => RuleErrors(r.Item1, r.Item2))
                .Concat(options.Policies.Keys.Where(string.IsNullOrWhiteSpace).Select(_ => "Policies içinde adı boş bir sınır var."))
                .ToArray();
            if (errors.Length > 0)
                throw new InvalidOperationException("CanRateLimitOptions geçersiz:\n- " + string.Join("\n- ", errors));
        }

        services.TryAddSingleton(options);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = OnRejectedAsync;

            if (options.Enabled)
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => Partition(c, "global", options.Global));

            limiter.AddPolicy(CanRateLimitPolicies.Auth, c => options.Enabled ? Partition(c, "auth", options.Auth) : RateLimitPartition.GetNoLimiter("off"));
            limiter.AddPolicy(CanRateLimitPolicies.Sensitive, c => options.Enabled ? Partition(c, "sensitive", options.Sensitive) : RateLimitPartition.GetNoLimiter("off"));

            foreach ((string name, RateLimitRule rule) in options.Policies)
                limiter.AddPolicy(name, c => options.Enabled ? Partition(c, $"p:{name}", rule) : RateLimitPartition.GetNoLimiter("off"));
        });

        // Uçlarda kullanılan politika adları açılışta denetlenir (ASP.NET bunu ancak ilk istekte fark eder).
        services.AddHostedService<RateLimitPolicyValidator>();

        return services;
    }

    /// <summary><c>UseAuthentication()</c> ve <c>UseCanTenantResolution()</c>'dan SONRA ekle (kullanıcı/tenant'a göre bölünsün).</summary>
    public static IApplicationBuilder UseCanRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        StartupValidation.Require<CanRateLimitOptions>(app, "UseCanRateLimiting", "builder.Services.AddCanRateLimiting()");
        return app.UseRateLimiter();
    }

    private static IEnumerable<string> RuleErrors(string name, RateLimitRule? rule)
    {
        if (rule is null)
        {
            yield return $"{name} boş olamaz.";
            yield break;
        }

        if (rule.PermitLimit <= 0)
            yield return $"{name}.PermitLimit sıfırdan büyük olmalı (şu an {rule.PermitLimit}).";
        if (rule.Window <= TimeSpan.Zero)
            yield return $"{name}.Window sıfırdan büyük olmalı.";
        if (rule.SegmentsPerWindow < 1)
            yield return $"{name}.SegmentsPerWindow en az 1 olmalı.";
    }

    private static RateLimitPartition<string> Partition(HttpContext context, string policy, RateLimitRule rule)
    {
        string tenant = context.RequestServices.GetService<TenantContext>()?.TenantId ?? "-";
        string by = rule.PartitionBy switch
        {
            RateLimitPartitionBy.Ip => $"ip:{Ip(context)}",
            RateLimitPartitionBy.Tenant => "tenant",
            _ => UserOrIp(context),
        };
        string partitionKey = $"{policy}:{by}:{tenant}";

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

/// <summary>
/// Açılışta uçların kullandığı hız sınırı adlarını denetler: tanımsız ad (yazım hatası, appsettings'te unutulmuş
/// politika) varsa hangi uçta hangi adın eksik olduğunu söyleyerek uygulamayı başlatmaz. Uygulamanın kendi
/// <c>AddRateLimiter(o =&gt; o.AddPolicy(...))</c> ile eklediği politikalar da tanımlı sayılır.
/// </summary>
internal sealed class RateLimitPolicyValidator(
    CanRateLimitOptions options,
    IOptions<RateLimiterOptions> limiterOptions,
    IServiceProvider services) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Uçlar ancak ASP.NET istek hattını kurduktan sonra (sunucu başlarken) görünür; bu yüzden denetim tüm servisler
    /// başladıktan sonra yapılır. Hata <c>app.Run()</c>/<c>StartAsync</c>'ten fırlar ve uygulama kapanır.
    /// </summary>
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<EndpointDataSource>() is not { } endpoints)
            return Task.CompletedTask;

        // ASP.NET kayıtlı politika adlarını dışarı açmıyor; okunamazsa (çerçeve değiştiyse) yanlış alarm vermemek için denetim atlanır.
        if (RegisteredPolicyNames(limiterOptions.Value) is not { } registered)
            return Task.CompletedTask;

        var known = new HashSet<string>(options.PolicyNames.Concat(registered), StringComparer.Ordinal); // ASP.NET adları birebir eşler
        string[] missing = endpoints.Endpoints
            .Select(e => (Endpoint: e, Policy: e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName))
            .Where(x => x.Policy is { Length: > 0 } && !known.Contains(x.Policy))
            .Select(x => $"'{x.Policy}' ({x.Endpoint.DisplayName})")
            .Distinct()
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Tanımsız hız sınırı kullanılıyor; appsettings'te RateLimiting:Policies altına ekle ya da adı düzelt:\n- "
                + string.Join("\n- ", missing));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static IReadOnlyCollection<string>? RegisteredPolicyNames(RateLimiterOptions limiter)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var names = new List<string>();
        bool found = false;
        foreach (string property in new[] { "PolicyMap", "UnactivatedPolicyMap" })
        {
            if (typeof(RateLimiterOptions).GetProperty(property, Flags)?.GetValue(limiter) is IDictionary map)
            {
                found = true;
                names.AddRange(map.Keys.OfType<string>());
            }
        }

        return found ? names : null;
    }
}
