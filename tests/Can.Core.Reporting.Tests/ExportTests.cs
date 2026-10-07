using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Can.Core.Reporting.Export;

namespace Can.Core.Reporting.Tests;

public sealed record WideItem(string Name, string Group, decimal Amount);

public class ExportTests
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static ReportExportOptions Options(Action<ReportExportOptions>? configure = null)
    {
        var options = new ReportExportOptions { Culture = Turkish, GeneratedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) };
        configure?.Invoke(options);
        return options;
    }

    private static ReportResult ByCategory() => SalesData.Run(new ReportDefinition
    {
        Title = "Kategori satışları",
        CalculatedFields = { new ReportCalculatedField("lineTotal", "[unitPrice] * [quantity] * (1 - [discount])") },
        Rows = { new ReportDimension("category") { Caption = "Kategori" } },
        Measures =
        {
            new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "revenue", Caption = "Ciro" },
            new ReportMeasure("discount", ReportAggregate.Average) { Name = "discount", Caption = "Ort. indirim" },
        },
    });

    private static ReportResult CategoryByYear() => SalesData.Run(new ReportDefinition
    {
        CalculatedFields = { new ReportCalculatedField("lineTotal", "[unitPrice] * [quantity] * (1 - [discount])") },
        Rows = { new ReportDimension("category") { Caption = "Kategori" } },
        Columns = { new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year } },
        Measures =
        {
            new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "revenue", Caption = "Ciro" },
            new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "share", Caption = "Pay", Display = MeasureDisplay.PercentOfGrandTotal, Format = "P1" },
        },
    });

    // ------------------------------------------------------------------ grid

    [Fact]
    public void Grid_merges_parent_column_headers_over_children_and_subtotal()
    {
        ReportResult result = SalesData.Run(SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("category"));
            d.Columns.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year });
            d.Columns.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Quarter });
        }));

        ReportGrid grid = ReportGrid.Build(result, Turkish);

        // 2024: Ç1 Ç2 Ç3 + ara toplam; 2025: Ç1 Ç2 + ara toplam; genel toplam
        Assert.Equal(2, grid.HeaderRows);
        Assert.Equal(1, grid.RowHeaderColumns);
        Assert.Equal(9, grid.ColumnCount);
        Assert.Equal(new[] { new GridMerge(0, 1, 4), new GridMerge(0, 5, 7) }, grid.Merges);
        Assert.Equal("2024", grid.Rows[0][1].Text);
        Assert.Equal("2025", grid.Rows[0][5].Text);
        Assert.Equal("Toplam", grid.Rows[1][4].Text);
        Assert.Equal("Genel toplam", grid.Rows[0][8].Text);
        Assert.Equal("category", grid.Rows[1][0].Text);
        Assert.Equal(GridStyle.Total, grid.Rows[^1][0].Style);
    }

    [Fact]
    public void Grid_text_uses_culture_and_format()
    {
        Assert.Equal("1.234,5", ReportGrid.Text(1234.5m, null, Turkish));
        Assert.Equal("%12,5", ReportGrid.Text(0.125m, "P1", Turkish).Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Equal(string.Empty, ReportGrid.Text(null, null, Turkish));
    }

    // ------------------------------------------------------------------ csv

    [Fact]
    public void Csv_has_bom_culture_separator_and_raw_numbers()
    {
        byte[] bytes = ReportExporter.Export(ByCategory(), ReportExportFormat.Csv, Options(o => o.CsvDelimiter = ";"));

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        string[] lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            new[] { "Kategori;Ciro;Ort. indirim", "Çeşni;40;0", "İçecek;206;0,025", "Şekerleme;165;0,166666666667", "Genel toplam;411;0,075" },
            lines);
    }

    [Fact]
    public void Csv_quotes_values_containing_the_delimiter()
    {
        byte[] bytes = ReportExporter.Export(ByCategory(), ReportExportFormat.Csv, Options(o => o.CsvDelimiter = ","));
        string text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("İçecek,206,\"0,025\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_type_and_extension()
    {
        Assert.Equal("text/csv", ReportExporter.ContentType(ReportExportFormat.Csv).Split(';')[0]);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ReportExporter.ContentType(ReportExportFormat.Xlsx));
        Assert.Equal("application/pdf", ReportExporter.ContentType(ReportExportFormat.Pdf));
        Assert.Equal(".xlsx", ReportExporter.FileExtension(ReportExportFormat.Xlsx));
    }

    // ------------------------------------------------------------------ xlsx

    private static Dictionary<string, string> Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void Xlsx_is_a_valid_package_with_values_merges_and_frozen_panes()
    {
        byte[] bytes = ReportExporter.Export(CategoryByYear(), ReportExportFormat.Xlsx, Options(o => o.Title = "Yıllık satış"));
        Dictionary<string, string> parts = Unzip(bytes);

        foreach (string part in new[] { "[Content_Types].xml", "_rels/.rels", "docProps/core.xml", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/worksheets/sheet1.xml", "xl/styles.xml" })
        {
            Assert.True(parts.ContainsKey(part), part);
            _ = XDocument.Parse(parts[part]); // iyi biçimli XML
        }

        string sheet = parts["xl/worksheets/sheet1.xml"];

        // başlık A1, boş satır, tablo 3. satırdan: 2 başlık satırı (yıl + ölçü), sonra Çeşni / İçecek / Şekerleme / Genel toplam
        Assert.Contains("Yıllık satış", sheet, StringComparison.Ordinal);
        Assert.Contains("ref=\"B3:C3\"", sheet, StringComparison.Ordinal);
        Assert.Contains("ref=\"D3:E3\"", sheet, StringComparison.Ordinal);
        Assert.Contains("ref=\"F3:G3\"", sheet, StringComparison.Ordinal);
        Assert.Contains("xSplit=\"1\" ySplit=\"4\" topLeftCell=\"B5\"", sheet, StringComparison.Ordinal);
        Assert.Matches(new Regex("<c r=\"D5\" s=\"\\d+\"><v>40(\\.0+)?</v>"), sheet);        // Çeşni 2025
        Assert.Matches(new Regex("<c r=\"F8\" s=\"\\d+\"><v>411(\\.0+)?</v>"), sheet);       // genel toplam
        Assert.Matches(new Regex("<c r=\"G8\" s=\"\\d+\"><v>1(\\.0+)?</v>"), sheet);         // pay %100
        Assert.DoesNotMatch(new Regex("<c r=\"B5\" s=\"\\d+\"><v>"), sheet);               // Çeşni 2024 boş

        string styles = parts["xl/styles.xml"];
        Assert.Contains("formatCode=\"0.0%\"", styles, StringComparison.Ordinal);
        Assert.Contains("formatCode=\"#,##0\"", styles, StringComparison.Ordinal);
        Assert.Contains("<sheet name=\"Yıllık satış\"", parts["xl/workbook.xml"], StringComparison.Ordinal);
    }

    [Fact]
    public void Xlsx_column_names_and_references()
    {
        Assert.Equal("A1", XlsxReportWriter.Reference(0, 0));
        Assert.Equal("Z10", XlsxReportWriter.Reference(9, 25));
        Assert.Equal("AA1", XlsxReportWriter.Reference(0, 26));
        Assert.Equal("XFD1", XlsxReportWriter.Reference(0, 16383));
    }

    // ------------------------------------------------------------------ pdf

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static List<string> ContentStreams(byte[] pdf)
    {
        string text = Latin1(pdf);
        var streams = new List<string>();
        foreach (Match match in Regex.Matches(text, "/Length (\\d+) /Filter /FlateDecode >>\nstream\n"))
        {
            int length = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            byte[] data = pdf.AsSpan(match.Index + match.Length, length).ToArray();
            using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            streams.Add(reader.ReadToEnd());
        }

        return streams;
    }

    private static int PageCount(byte[] pdf) =>
        int.Parse(Regex.Match(Latin1(pdf), "/Type /Pages /Kids \\[[^\\]]*\\] /Count (\\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    [Fact]
    public void Pdf_has_valid_structure_and_cross_reference_offsets()
    {
        byte[] pdf = ReportExporter.Export(ByCategory(), ReportExportFormat.Pdf, Options());
        string text = Latin1(pdf);

        Assert.StartsWith("%PDF-1.7\n", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

        int startxref = int.Parse(Regex.Match(text, "startxref\n(\\d+)\n%%EOF").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n", text[startxref..], StringComparison.Ordinal);

        Match header = Regex.Match(text[startxref..], "xref\n0 (\\d+)\n");
        int count = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
        string[] entries = text[(startxref + header.Length)..].Split('\n').Skip(1).Take(count - 1).ToArray();
        Assert.Equal(count - 1, entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            int offset = int.Parse(entries[i][..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i + 1} 0 obj\n", text[offset..], StringComparison.Ordinal);
        }

        Assert.Equal(1, PageCount(pdf));
        Assert.Contains("/Differences [", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_encodes_turkish_characters_with_custom_encoding()
    {
        byte[] pdf = ReportExporter.Export(ByCategory(), ReportExportFormat.Pdf, Options(o => o.Title = "Satış raporu"));
        string content = Assert.Single(ContentStreams(pdf));

        Assert.Contains("(\\307e\\376ni)", content, StringComparison.Ordinal);       // Çeşni: Ç=199, ş=254
        Assert.Contains("(\\335\\347ecek)", content, StringComparison.Ordinal);      // İçecek: İ=221, ç=231
        Assert.Contains("(\\336ekerleme)", content, StringComparison.Ordinal);       // Şekerleme: Ş=222
        Assert.Contains("(Sat\\375\\376 raporu)", content, StringComparison.Ordinal); // ı=253
        Assert.Contains("(Sayfa 1 / 1)", content, StringComparison.Ordinal);
        Assert.Contains("(411)", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_splits_long_reports_into_pages_and_repeats_headers()
    {
        WideItem[] items = Enumerable.Range(1, 150).Select(i => new WideItem($"Ürün {i:000}", "G", i)).ToArray();
        ReportResult result = SalesData.Engine().Run(new ReportDefinition
        {
            Rows = { new ReportDimension("name") { Caption = "Ad" } },
            Measures = { new ReportMeasure("amount", ReportAggregate.Sum) { Caption = "Tutar" } },
        }, ReportDataSet.From(items));

        byte[] pdf = ReportExporter.Export(result, ReportExportFormat.Pdf, Options(o => o.PdfPageSize = PdfPageSize.A4Portrait));
        List<string> pages = ContentStreams(pdf);

        Assert.True(pages.Count > 1);
        Assert.Equal(pages.Count, PageCount(pdf));
        for (int i = 0; i < pages.Count; i++)
        {
            Assert.Contains("(Tutar)", pages[i], StringComparison.Ordinal);
            Assert.Contains($"(Sayfa {i + 1} / {pages.Count})", pages[i], StringComparison.Ordinal);
        }

        Assert.Contains("(\\334r\\374n 001)", pages[0], StringComparison.Ordinal); // Ü=220, ü=252
        Assert.Contains("(\\334r\\374n 150)", pages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Pdf_splits_wide_reports_by_columns_and_repeats_row_headers()
    {
        WideItem[] items = Enumerable.Range(1, 60)
            .SelectMany(i => new[] { new WideItem($"Sütun başlığı {i:00}", "Birinci", i), new WideItem($"Sütun başlığı {i:00}", "İkinci", i * 1000) })
            .ToArray();
        ReportResult result = SalesData.Engine().Run(new ReportDefinition
        {
            Rows = { new ReportDimension("group") { Caption = "Grup" } },
            Columns = { new ReportDimension("name") },
            Measures = { new ReportMeasure("amount", ReportAggregate.Sum) },
        }, ReportDataSet.From(items));

        byte[] pdf = ReportExporter.Export(result, ReportExportFormat.Pdf, Options());
        List<string> pages = ContentStreams(pdf);

        Assert.True(pages.Count > 1);
        Assert.All(pages, page =>
        {
            Assert.Contains("(Birinci)", page, StringComparison.Ordinal);
            Assert.Contains("(\\335kinci)", page, StringComparison.Ordinal);
            Assert.Contains("(Grup)", page, StringComparison.Ordinal);
        });
        Assert.Contains("(Genel toplam)", pages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_writes_to_stream()
    {
        using var stream = new MemoryStream();
        await ReportExporter.ExportAsync(ByCategory(), ReportExportFormat.Xlsx, stream, Options());

        Assert.Equal((byte)'P', stream.ToArray()[0]);
        Assert.Equal((byte)'K', stream.ToArray()[1]);
    }
}
