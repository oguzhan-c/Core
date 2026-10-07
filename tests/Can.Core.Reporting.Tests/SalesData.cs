using System.Globalization;

namespace Can.Core.Reporting.Tests;

public sealed record Sale(DateTime OrderDate, string Category, string Product, string Country, decimal UnitPrice, int Quantity, decimal Discount, string? Customer);

/// <summary>
/// 8 satır; satır tutarı = birim fiyat × adet × (1 - indirim):
/// <code>
///  1 2024-01-15 İçecek    Çay      TR 10×5        = 50   A
///  2 2024-02-10 İçecek    Kahve    DE 20×2 %10    = 36   B
///  3 2024-04-05 İçecek    Çay      DE 10×10       = 100  A
///  4 2024-07-20 Şekerleme Çikolata TR 15×4        = 60   C
///  5 2025-01-03 Şekerleme Çikolata TR 15×6 %50    = 45   A
///  6 2025-03-12 İçecek    Kahve    TR 20×1        = 20   -
///  7 2025-05-30 Çeşni     Tuz      DE 5×8         = 40   B
///  8 2025-06-01 Şekerleme Lokum    DE 30×2        = 60   C
/// </code>
/// Kategori: Çeşni 40, İçecek 206, Şekerleme 165; yıl: 2024 246, 2025 165; toplam 411.
/// </summary>
internal static class SalesData
{
    public static readonly Sale[] Rows =
    [
        new(new DateTime(2024, 1, 15), "İçecek", "Çay", "TR", 10m, 5, 0m, "A"),
        new(new DateTime(2024, 2, 10), "İçecek", "Kahve", "DE", 20m, 2, 0.1m, "B"),
        new(new DateTime(2024, 4, 5), "İçecek", "Çay", "DE", 10m, 10, 0m, "A"),
        new(new DateTime(2024, 7, 20), "Şekerleme", "Çikolata", "TR", 15m, 4, 0m, "C"),
        new(new DateTime(2025, 1, 3), "Şekerleme", "Çikolata", "TR", 15m, 6, 0.5m, "A"),
        new(new DateTime(2025, 3, 12), "İçecek", "Kahve", "TR", 20m, 1, 0m, null),
        new(new DateTime(2025, 5, 30), "Çeşni", "Tuz", "DE", 5m, 8, 0m, "B"),
        new(new DateTime(2025, 6, 1), "Şekerleme", "Lokum", "DE", 30m, 2, 0m, "C"),
    ];

    public static ReportDataSet DataSet => ReportDataSet.From(Rows);

    public static ReportEngine Engine(int maxCells = 2_000_000) =>
        new(new ReportEngineOptions { Culture = CultureInfo.GetCultureInfo("tr-TR"), MaxCells = maxCells });

    /// <summary>lineTotal hesaplanmış alanı ve ciro toplamı.</summary>
    public static ReportDefinition WithLineTotal(Action<ReportDefinition>? configure = null)
    {
        var definition = new ReportDefinition
        {
            CalculatedFields = { new ReportCalculatedField("lineTotal", "[unitPrice] * [quantity] * (1 - [discount])") },
            Measures = { new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "revenue" } },
        };
        configure?.Invoke(definition);
        return definition;
    }

    public static ReportResult Run(ReportDefinition definition) => Engine().Run(definition, DataSet);

    public static string[] RowLabels(this ReportResult result) => result.Rows.Select(r => r.Label).ToArray();

    public static string[] ColumnLabels(this ReportResult result) => result.Columns.Select(c => c.Label).ToArray();

    /// <summary>Tek sütunlu raporda bir değerin satır değerleri.</summary>
    public static decimal?[] Column(this ReportResult result, int column = 0, int measure = 0) =>
        Enumerable.Range(0, result.Rows.Count).Select(r => (decimal?)result.Value(r, column, measure)).ToArray();
}
