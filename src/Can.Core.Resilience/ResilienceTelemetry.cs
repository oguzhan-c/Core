using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Can.Core.Resilience;

/// <summary>
/// Strateji olayları: <c>can.resilience.events</c> sayacı (etiketler: <c>can.resilience.strategy</c>,
/// <c>can.resilience.event</c>, <c>can.resilience.operation</c>) ve o anki span'a olay olarak eklenir.
/// Olaylar: retry, timeout, circuit opened/half-opened/closed/rejected, rate limiter rejected, fallback, hedging.
/// </summary>
public static class ResilienceTelemetry
{
    public const string Name = "Can.Core.Resilience";

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Events =
        Meter.CreateCounter<long>("can.resilience.events", unit: "{event}", description: "Dayanıklılık stratejilerinin olayları");

    internal static void Report(string strategy, string eventName, ResilienceContext? context)
    {
        if (Events.Enabled)
        {
            Events.Add(
                1,
                new KeyValuePair<string, object?>("can.resilience.strategy", strategy),
                new KeyValuePair<string, object?>("can.resilience.event", eventName),
                new KeyValuePair<string, object?>("can.resilience.operation", context?.OperationKey)
            );
        }

        Activity.Current?.AddEvent(new ActivityEvent($"resilience.{strategy}.{eventName}"));
    }
}
