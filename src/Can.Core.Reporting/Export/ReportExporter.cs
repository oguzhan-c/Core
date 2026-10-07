using System.Globalization;
using System.Text.Json.Serialization;

namespace Can.Core.Reporting.Export;

[JsonConverter(typeof(JsonStringEnumConverter<ReportExportFormat>))]
public enum ReportExportFormat
{
    Csv,
    Xlsx,
    Pdf,
}

public enum PdfPageSize
{
    A4Landscape,
    A4Portrait,
    A3Landscape,
}

public sealed class ReportExportOptions
{
    /// <summary>Sayı/tarih biçimleri ve CSV ayırıcısı için kültür (boşsa geçerli kültür).</summary>
    public CultureInfo? Culture { get; set; }

    /// <summary>Başlık (boşsa rapor başlığı).</summary>
    public string? Title { get; set; }

    /// <summary>Başlığın altındaki satır (ör. filtre özeti).</summary>
    public string? Subtitle { get; set; }

    public DateTimeOffset? GeneratedAt { get; set; }

    /// <summary>CSV ayırıcısı (boşsa kültürün liste ayırıcısı: Türkçe'de <c>;</c>, Excel böyle açar).</summary>
    public string? CsvDelimiter { get; set; }

    public PdfPageSize PdfPageSize { get; set; } = PdfPageSize.A4Landscape;

    /// <summary>PDF yazı boyutu (sığmazsa 6 pt'ye kadar küçülür, yine sığmazsa sütunlar sayfalara bölünür).</summary>
    public double PdfFontSize { get; set; } = 8;

    /// <summary>Ara toplam sütunu başlığı.</summary>
    public string TotalLabel { get; set; } = "Toplam";

    /// <summary>PDF sayfa altı: <c>{0}</c> sayfa, <c>{1}</c> toplam sayfa.</summary>
    public string PageLabel { get; set; } = "Sayfa {0} / {1}";
}

/// <summary>
/// Rapor sonucunu dosyaya yazar. Üç biçim de dış paket olmadan üretilir: CSV düz metin, XLSX bir ZIP içindeki
/// OpenXML dosyaları, PDF kendi yazıcımız (Helvetica + Türkçe karakterler için Windows-1254 kodlaması).
/// </summary>
public static class ReportExporter
{
    public static string ContentType(ReportExportFormat format) =>
        format switch
        {
            ReportExportFormat.Csv => "text/csv; charset=utf-8",
            ReportExportFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/pdf",
        };

    public static string FileExtension(ReportExportFormat format) =>
        format switch
        {
            ReportExportFormat.Csv => ".csv",
            ReportExportFormat.Xlsx => ".xlsx",
            _ => ".pdf",
        };

    public static async Task ExportAsync(ReportResult result, ReportExportFormat format, Stream output, ReportExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new ReportExportOptions();

        byte[] bytes = Export(result, format, options);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static byte[] Export(ReportResult result, ReportExportFormat format, ReportExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new ReportExportOptions();
        CultureInfo culture = options.Culture ?? CultureInfo.CurrentCulture;
        ReportGrid grid = ReportGrid.Build(result, culture, options.TotalLabel);

        return format switch
        {
            ReportExportFormat.Csv => CsvReportWriter.Write(grid, culture, options),
            ReportExportFormat.Xlsx => XlsxReportWriter.Write(grid, result, culture, options),
            _ => PdfReportWriter.Write(grid, result, culture, options),
        };
    }
}

internal static class CsvReportWriter
{
    public static byte[] Write(ReportGrid grid, CultureInfo culture, ReportExportOptions options)
    {
        string delimiter = options.CsvDelimiter ?? culture.TextInfo.ListSeparator;
        var builder = new System.Text.StringBuilder();

        foreach (GridCell[] row in grid.Rows)
        {
            for (int i = 0; i < row.Length; i++)
            {
                if (i > 0)
                    builder.Append(delimiter);
                builder.Append(Escape(CsvText(row[i], culture), delimiter));
            }

            builder.Append("\r\n");
        }

        // BOM: Excel UTF-8'i (Türkçe karakterler) ancak böyle tanır
        return [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(builder.ToString())];
    }

    /// <summary>Sayılar binlik ayırıcısız, kültürün ondalık ayırıcısıyla (yeniden hesaplanabilsin); yüzdeler oran olarak.</summary>
    private static string CsvText(GridCell cell, CultureInfo culture) =>
        cell.Value switch
        {
            decimal d => d.ToString("0.############", culture),
            DateTime t => t.TimeOfDay == TimeSpan.Zero ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => cell.Text,
        };

    private static string Escape(string text, string delimiter) =>
        text.Contains(delimiter, StringComparison.Ordinal) || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;
}
