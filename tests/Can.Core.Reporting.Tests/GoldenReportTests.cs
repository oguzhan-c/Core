using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Can.Core.Reporting.Tests;

/// <summary>
/// Altın (golden) testler: bilinen tanımların TÜM sonucu (başlıklar + hücreler) <c>Golden/*.txt</c> dosyalarıyla
/// karşılaştırılır. Motorda yapılan bir değişiklik sonucu beklenmedik biçimde değiştirirse test kırılır.
/// Dosya yoksa ya da <c>CAN_UPDATE_GOLDEN=1</c> ise dosya yazılır (gözden geçirip commit'le).
/// </summary>
public sealed class GoldenReportTests
{
    public static TheoryData<string> Names => new() { "pivot-displays", "top-n-others-having", "date-groupings", "statistics" };

    [Theory]
    [MemberData(nameof(Names))]
    public void Result_matches_golden_file(string name)
    {
        ReportResult result = SalesData.Run(Definition(name));

        AssertGolden(name, Render(result));
    }

    private static ReportDefinition Definition(string name) => name switch
    {
        "pivot-displays" => SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("category"));
            d.Rows.Add(new ReportDimension("product"));
            d.Columns.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "rowShare", Display = MeasureDisplay.PercentOfRowTotal });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "colShare", Display = MeasureDisplay.PercentOfColumnTotal });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "parentShare", Display = MeasureDisplay.PercentOfParentRow });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "rank", Display = MeasureDisplay.Rank });
        }),
        "top-n-others-having" => SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("category") { Sort = DimensionSort.MeasureDescending });
            d.Rows.Add(new ReportDimension("customer") { Sort = DimensionSort.MeasureDescending, Top = 1 });
            d.Columns.Add(new ReportDimension("country"));
            d.Measures.Add(new ReportMeasure(null, ReportAggregate.Count) { Name = "lines" });
            d.Having.Add(new ReportHaving("revenue", FilterOperator.GreaterThanOrEqual, [40m]));
        }),
        "date-groupings" => SalesData.WithLineTotal(d =>
        {
            d.Filters.Add(ReportFilter.In("country", "TR", "DE"));
            d.Rows.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.YearQuarter });
            d.Columns.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.DayOfWeek });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "running", Display = MeasureDisplay.RunningTotal });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "diff", Display = MeasureDisplay.DifferenceFromPrevious });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "change", Display = MeasureDisplay.PercentDifferenceFromPrevious });
        }),
        "statistics" => new ReportDefinition
        {
            Rows = { new ReportDimension("unitPrice") { RangeSize = 10 } },
            Measures =
            {
                new ReportMeasure("quantity", ReportAggregate.Median),
                new ReportMeasure("quantity", ReportAggregate.Percentile) { Percentile = 0.9 },
                new ReportMeasure("quantity", ReportAggregate.StdDev),
                new ReportMeasure("quantity", ReportAggregate.VarP),
                new ReportMeasure("customer", ReportAggregate.CountDistinct),
                new ReportMeasure("product", ReportAggregate.First),
                new ReportMeasure("product", ReportAggregate.Last),
                new ReportMeasure("orderDate", ReportAggregate.Min),
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Sonucu okunabilir, kültürden bağımsız bir metne çevirir (sayılar 10 basamağa yuvarlanır, sondaki sıfırlar atılır: 36.0 ile 36 aynı).</summary>
    private static string Render(ReportResult result)
    {
        var text = new StringBuilder();
        text.Append("rows: ").AppendJoin(" | ", result.RowDimensions.Select(d => d.Caption)).Append('\n');
        text.Append("columns: ").AppendJoin(" | ", result.ColumnDimensions.Select(d => d.Caption)).Append('\n');
        text.Append("measures: ").AppendJoin(" | ", result.Measures.Select(m => $"{m.Name}:{m.Aggregate}:{m.Display}:{m.Format}")).Append('\n');
        text.Append("source: ").Append(result.SourceRowCount).Append(", matched: ").Append(result.MatchedRowCount).Append('\n');
        text.Append('\n');

        for (int c = 0; c < result.Columns.Count; c++)
            text.Append("C").Append(c).Append(' ').Append(Header(result.Columns[c])).Append('\n');
        text.Append('\n');

        for (int r = 0; r < result.Rows.Count; r++)
        {
            text.Append("R").Append(r).Append(' ').Append(Header(result.Rows[r])).Append('\n');
            for (int c = 0; c < result.Columns.Count; c++)
            {
                text.Append("  C").Append(c).Append(':');
                for (int m = 0; m < result.Measures.Count; m++)
                    text.Append(' ').Append(Format(result.Value(r, c, m)));
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private static string Header(ReportHeader header) =>
        $"{header.Kind} L{header.Level} [{string.Join(" / ", header.Labels)}]";

    private static string Format(object? value) => value switch
    {
        null => "-",
        decimal d => Math.Round(d, 10).ToString("0.##########", CultureInfo.InvariantCulture),
        double d => Math.Round(d, 10).ToString("0.##########", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "-",
    };

    private static void AssertGolden(string name, string actual, [CallerFilePath] string source = "")
    {
        string path = Path.Combine(Path.GetDirectoryName(source)!, "Golden", name + ".txt");
        bool update = Environment.GetEnvironmentVariable("CAN_UPDATE_GOLDEN") == "1";
        if (update || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }
}
