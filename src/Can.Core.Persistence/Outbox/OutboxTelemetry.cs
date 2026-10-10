using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Can.Core.Persistence.Outbox;

/// <summary>Outbox yayını için span ve sayaçlar.</summary>
public static class OutboxTelemetry
{
    public const string Name = "Can.Core.Persistence";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>
    /// İşlenen outbox mesajları. Etiketler: <c>can.event</c>, <c>can.outcome</c> (<c>published</c> / <c>failed</c> /
    /// <c>dead</c>). <c>dead</c> sayacı sıfırdan büyükse alarm kurulmalı.
    /// </summary>
    public static readonly Counter<long> Messages =
        Meter.CreateCounter<long>("can.outbox.messages", unit: "{message}", description: "Yayınlanan, hata alan ya da ölen outbox mesajları");

    /// <summary>
    /// Event'in oluşmasından yayınlanmasına kadar geçen süre (saniye). Yükselmesi outbox'ın geride kaldığını gösterir.
    /// </summary>
    public static readonly Histogram<double> Lag =
        Meter.CreateHistogram<double>("can.outbox.lag", unit: "s", description: "Event oluşumu ile yayını arasındaki süre");
}
