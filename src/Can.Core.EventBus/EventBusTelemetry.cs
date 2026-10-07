using System.Diagnostics;

namespace Can.Core.EventBus;

/// <summary>
/// Event bus span'ları. Yayınlayan tarafın iz bilgisi (W3C <c>traceparent</c> / <c>tracestate</c>) zarfın header'larına
/// yazılır; dinleyen taraf span'ını bu ize bağlar. Böylece servisler arası tek bir dağıtık iz (trace) oluşur.
/// </summary>
public static class EventBusTelemetry
{
    public const string Name = "Can.Core.EventBus";
    public const string TraceParentHeader = "traceparent";
    public const string TraceStateHeader = "tracestate";

    public static readonly ActivitySource Source = new(Name);

    internal static Activity? StartPublish(string eventName)
    {
        Activity? activity = Source.StartActivity($"publish {eventName}", ActivityKind.Producer);
        activity?.SetTag("messaging.operation.type", "publish");
        activity?.SetTag("messaging.destination.name", eventName);
        return activity;
    }

    /// <summary>O anki iz bilgisi (yayın span'ı yoksa üstteki span'ınki).</summary>
    internal static Dictionary<string, string> CreateHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C } current)
        {
            headers[TraceParentHeader] = current.Id!;
            if (!string.IsNullOrEmpty(current.TraceStateString))
                headers[TraceStateHeader] = current.TraceStateString;
        }

        return headers;
    }

    internal static Activity? StartProcess(EventEnvelope envelope)
    {
        if (!Source.HasListeners())
            return null;

        ActivityContext parent = default;
        if (envelope.Headers.TryGetValue(TraceParentHeader, out string? traceParent))
        {
            envelope.Headers.TryGetValue(TraceStateHeader, out string? traceState);
            ActivityContext.TryParse(traceParent, traceState, isRemote: true, out parent);
        }

        Activity? activity = Source.StartActivity($"process {envelope.EventName}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.destination.name", envelope.EventName);
        activity?.SetTag("messaging.message.id", envelope.EventId.ToString());
        if (envelope.TenantId is not null)
            activity?.SetTag("can.tenant", envelope.TenantId);
        return activity;
    }
}
