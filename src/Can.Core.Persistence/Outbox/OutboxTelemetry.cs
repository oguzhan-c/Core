using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Can.Core.Persistence.Outbox;

/// <summary>Outbox yayını için span ve sayaçlar.</summary>
public static class OutboxTelemetry
{
    public const string Name = "Can.Core.Persistence";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>İşlenen outbox mesajları. Etiketler: <c>can.event</c>, <c>can.outcome</c> (published / failed).</summary>
    public static readonly Counter<long> Messages =
        Meter.CreateCounter<long>("can.outbox.messages", unit: "{message}", description: "Yayınlanan ya da hata alan outbox mesajları");
}
