using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Can.Core.EventBus;

/// <summary>Taşıyıcının anlık durumu (her broker paketi kendi ölçümünü yapar).</summary>
public interface IEventBrokerHealth
{
    Task<EventBrokerHealthReport> CheckAsync(CancellationToken cancellationToken = default);
}

/// <param name="Broker">Taşıyıcı (<c>rabbitmq</c>, <c>kafka</c>, <c>azure-servicebus</c>).</param>
/// <param name="Reachable">Broker'a ulaşılıyor mu.</param>
/// <param name="Consuming">Tüketici dinliyor mu; tüketici kapalıysa ya da dinlenecek event yoksa <see langword="null"/>.</param>
/// <param name="DeadLetters">DLQ'daki mesaj sayısı; bilinmiyorsa <see langword="null"/>.</param>
/// <param name="Error">Ulaşılamadıysa hata.</param>
public sealed record EventBrokerHealthReport(string Broker, bool Reachable, bool? Consuming, long? DeadLetters, string? Error = null);

/// <summary>Event bus sağlık kontrolünün eşikleri.</summary>
public sealed class EventBusHealthCheckOptions
{
    /// <summary>DLQ'daki mesaj bundan fazlaysa <see cref="HealthStatus.Degraded"/> (varsayılan 0: tek ölü mesaj bile).</summary>
    public long DeadLetterThreshold { get; set; }

    /// <summary>Broker'a ulaşılamazsa ya da tüketici dinlemiyorsa (varsayılan <see cref="HealthStatus.Unhealthy"/>).</summary>
    public HealthStatus FailureStatus { get; set; } = HealthStatus.Unhealthy;
}

/// <summary>
/// Event bus'ın durumu: broker'a ulaşılmıyorsa ya da tüketici dinlemiyorsa <see cref="EventBusHealthCheckOptions.FailureStatus"/>,
/// DLQ'da eşikten fazla mesaj varsa <see cref="HealthStatus.Degraded"/>. Bellek içi taşıyıcıda her zaman sağlıklı.
/// </summary>
public sealed class EventBusHealthCheck : IHealthCheck
{
    private readonly IEventBrokerHealth? _broker;
    private readonly EventBusHealthCheckOptions _options;

    public EventBusHealthCheck(IEventBrokerHealth? broker, EventBusHealthCheckOptions options)
    {
        _broker = broker;
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_broker is null)
            return HealthCheckResult.Healthy("Bellek içi taşıyıcı.");

        EventBrokerHealthReport report = await _broker.CheckAsync(cancellationToken).ConfigureAwait(false);
        var data = new Dictionary<string, object>(StringComparer.Ordinal) { ["broker"] = report.Broker };
        if (report.Consuming is { } consuming)
            data["consuming"] = consuming;
        if (report.DeadLetters is { } dead)
            data["deadLetters"] = dead;

        if (!report.Reachable)
            return new HealthCheckResult(_options.FailureStatus, $"{report.Broker}'a ulaşılamıyor: {report.Error}", data: data);
        if (report.Consuming == false)
            return new HealthCheckResult(_options.FailureStatus, $"{report.Broker} tüketicisi dinlemiyor.", data: data);
        if (report.DeadLetters > _options.DeadLetterThreshold)
            return HealthCheckResult.Degraded($"DLQ'da {report.DeadLetters} mesaj var: nedeni giderip yeniden gönder (IDeadLetterQueue.ReplayAsync).", data: data);
        return HealthCheckResult.Healthy(null, data);
    }
}

public static class EventBusHealthCheckExtensions
{
    /// <summary>
    /// Event bus sağlık kontrolü (broker bağlantısı, tüketici, DLQ). Etiket verilmezse <c>ready</c>.
    /// </summary>
    /// <example><code>builder.Services.AddHealthChecks().AddCanEventBusCheck(o =&gt; o.DeadLetterThreshold = 10);</code></example>
    public static IHealthChecksBuilder AddCanEventBusCheck(
        this IHealthChecksBuilder builder,
        Action<EventBusHealthCheckOptions>? configure = null,
        string name = "eventbus",
        params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new EventBusHealthCheckOptions();
        configure?.Invoke(options);
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new EventBusHealthCheck(sp.GetService<IEventBrokerHealth>(), options),
            options.FailureStatus,
            tags.Length > 0 ? tags : ["ready"],
            TimeSpan.FromSeconds(10)));
    }
}
