using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CanFx.Reporting;

/// <summary>Rapor çalıştırma için span ve metrikler.</summary>
public static class ReportingTelemetry
{
    public const string Name = "CanFx.Reporting";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>
    /// Çalışan raporlar. Etiketler: <c>can.report.source</c>, <c>can.report.mode</c> (<c>database</c> / <c>memory</c>),
    /// <c>can.report.fallback</c> (<see cref="ReportModeReason.Code"/>; veritabanı modunda yok), <c>can.outcome</c>
    /// (<c>success</c> / <c>invalid</c> / <c>denied</c> / <c>error</c>).
    /// </summary>
    public static readonly Counter<long> Runs =
        Meter.CreateCounter<long>("can.report.runs", unit: "{report}", description: "Çalışan raporlar (mod ve belleğe düşme nedeniyle)");

    /// <summary>Rapor süresi (saniye); etiketler <see cref="Runs"/> ile aynı.</summary>
    public static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("can.report.duration", unit: "s", description: "Rapor çalıştırma süresi");
}
