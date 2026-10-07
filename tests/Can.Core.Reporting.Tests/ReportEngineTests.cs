using System.Text.Json;

namespace Can.Core.Reporting.Tests;

public class ReportEngineTests
{
    [Fact]
    public void Groups_rows_with_calculated_field_and_grand_total()
    {
        ReportResult result = SalesData.Run(SalesData.WithLineTotal(d => d.Rows.Add(new ReportDimension("category"))));

        // Türkçe sıralama: Ç < İ < Ş
        Assert.Equal(new[] { "Çeşni", "İçecek", "Şekerleme", "Genel toplam" }, result.RowLabels());
        Assert.Equal(new decimal?[] { 40, 206, 165, 411 }, result.Column());
        Assert.Equal(ReportHeaderKind.GrandTotal, result.Rows[^1].Kind);
        Assert.Equal(8, result.SourceRowCount);
        Assert.Equal(8, result.MatchedRowCount);
    }

    [Fact]
    public void Cross_tab_by_year_has_row_and_column_totals()
    {
        ReportResult result = SalesData.Run(SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("category"));
            d.Columns.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year });
        }));

        Assert.Equal(new[] { "2024", "2025", "Genel toplam" }, result.ColumnLabels());
        Assert.Equal(new decimal?[] { null, 186, 60, 246 }, result.Column(0));
        Assert.Equal(new decimal?[] { 40, 20, 105, 165 }, result.Column(1));
        Assert.Equal(new decimal?[] { 40, 206, 165, 411 }, result.Column(2));
        Assert.Equal(2024, result.Columns[0].Keys[0]);
    }

    [Fact]
    public void Nested_rows_put_group_header_before_items_and_subtotals_can_be_hidden()
    {
        ReportDefinition definition = SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("category"));
            d.Rows.Add(new ReportDimension("product"));
        });

        ReportResult result = SalesData.Run(definition);
        Assert.Equal(new[] { "Çeşni", "Tuz", "İçecek", "Çay", "Kahve", "Şekerleme", "Çikolata", "Lokum", "Genel toplam" }, result.RowLabels());
        Assert.Equal(new decimal?[] { 40, 40, 206, 150, 56, 165, 105, 60, 411 }, result.Column());
        Assert.Equal(ReportHeaderKind.Group, result.Rows[2].Kind);
        Assert.Equal(new object?[] { "İçecek", "Çay" }, result.Rows[3].Keys);

        definition.ShowSubtotals = false;
        Assert.Equal(new[] { "Tuz", "Çay", "Kahve", "Çikolata", "Lokum", "Genel toplam" }, SalesData.Run(definition).RowLabels());
    }

    [Fact]
    public void Column_subtotals_come_after_their_children()
    {
        ReportResult result = SalesData.Run(SalesData.WithLineTotal(d =>
        {
            d.Columns.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year });
            d.Columns.Add(new ReportDimension("country"));
        }));

        Assert.Equal(new[] { "DE", "TR", "2024", "DE", "TR", "2025", "Genel toplam" }, result.ColumnLabels());
        // 2024: DE 36+100, TR 50+60 | 2025: DE 40+60, TR 45+20
        Assert.Equal(new object?[] { 136m, 110m, 246m, 100m, 65m, 165m, 411m }, result.Values[0]);
    }

    [Fact]
    public void Statistical_aggregates()
    {
        var definition = new ReportDefinition
        {
            Measures =
            {
                new ReportMeasure("quantity", ReportAggregate.Sum),
                new ReportMeasure("quantity", ReportAggregate.Median),
                new ReportMeasure("quantity", ReportAggregate.Percentile) { Percentile = 0.9 },
                new ReportMeasure("quantity", ReportAggregate.Var),
                new ReportMeasure("quantity", ReportAggregate.VarP),
                new ReportMeasure("quantity", ReportAggregate.StdDevP),
                new ReportMeasure("customer", ReportAggregate.CountDistinct),
                new ReportMeasure("customer", ReportAggregate.Count),
                new ReportMeasure(null, ReportAggregate.Count),
                new ReportMeasure("orderDate", ReportAggregate.Min),
                new ReportMeasure("orderDate", ReportAggregate.Max),
                new ReportMeasure("product", ReportAggregate.First),
                new ReportMeasure("product", ReportAggregate.Last),
                new ReportMeasure("unitPrice", ReportAggregate.Average),
            },
        };

        ReportResult r = SalesData.Run(definition);
        decimal Number(int measure) => (decimal)r.Value(0, 0, measure)!;

        // adetler sıralı: 1 2 2 4 5 6 8 10
        Assert.Equal(38m, Number(0));
        Assert.Equal(4.5m, Number(1));
        Assert.Equal(8.6m, Number(2)); // konum 0.9×7 = 6.3 → 8 + 0.3×(10-8)
        Assert.Equal(69.5 / 7, (double)Number(3), 6);
        Assert.Equal(69.5 / 8, (double)Number(4), 6);
        Assert.Equal(Math.Sqrt(69.5 / 8), (double)Number(5), 6);
        Assert.Equal(3m, Number(6)); // A, B, C (boş sayılmaz)
        Assert.Equal(7m, Number(7)); // boş olmayan müşteri
        Assert.Equal(8m, Number(8)); // satır
        Assert.Equal(new DateTime(2024, 1, 15), r.Value(0, 0, 9));
        Assert.Equal(new DateTime(2025, 6, 1), r.Value(0, 0, 10));
        Assert.Equal("Çay", r.Value(0, 0, 11));
        Assert.Equal("Lokum", r.Value(0, 0, 12));
        Assert.Equal(15.625m, Number(13));
    }

    [Fact]
    public void Date_groupings_use_culture_names()
    {
        ReportResult quarters = SalesData.Run(SalesData.WithLineTotal(d => d.Rows.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Quarter })));
        Assert.Equal(new[] { "Ç1", "Ç2", "Ç3", "Genel toplam" }, quarters.RowLabels());
        Assert.Equal(new decimal?[] { 151, 200, 60, 411 }, quarters.Column());

        ReportResult months = SalesData.Run(SalesData.WithLineTotal(d => d.Rows.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.YearMonth })));
        Assert.Equal("Ocak 2024", months.Rows[0].Label);
        Assert.Equal(202401, months.Rows[0].Keys[0]);

        ReportResult days = SalesData.Run(SalesData.WithLineTotal(d => d.Rows.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.DayOfWeek })));
        Assert.Equal("Pazartesi", days.Rows[0].Label); // 15.01.2024 pazartesi

        ReportResult yearQuarter = SalesData.Run(SalesData.WithLineTotal(d => d.Rows.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.YearQuarter })));
        Assert.Equal(new[] { "2024 Ç1", "2024 Ç2", "2024 Ç3", "2025 Ç1", "2025 Ç2", "Genel toplam" }, yearQuarter.RowLabels());
    }

    [Fact]
    public void Display_types_along_rows()
    {
        ReportResult r = SalesData.Run(SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "share", Display = MeasureDisplay.PercentOfColumnTotal });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "running", Display = MeasureDisplay.RunningTotal });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "diff", Display = MeasureDisplay.DifferenceFromPrevious });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "change", Display = MeasureDisplay.PercentDifferenceFromPrevious });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "rank", Display = MeasureDisplay.Rank });
        }));

        // 2024: 246, 2025: 165, toplam: 411
        Assert.Equal(246m / 411m, r.Value(0, 0, "share"));
        Assert.Equal(1m, r.Value(2, 0, "share"));
        Assert.Equal(new object?[] { 246m, 411m, 411m }, Enumerable.Range(0, 3).Select(i => r.Value(i, 0, "running")));
        Assert.Equal(new object?[] { null, -81m, null }, Enumerable.Range(0, 3).Select(i => r.Value(i, 0, "diff")));
        Assert.Equal(-81m / 246m, r.Value(1, 0, "change"));
        Assert.Equal(new object?[] { 1m, 2m, null }, Enumerable.Range(0, 3).Select(i => r.Value(i, 0, "rank")));
        Assert.Equal("P1", r.Measures[1].Format);
    }

    [Fact]
    public void Percent_of_parent_and_running_total_along_columns()
    {
        ReportResult r = SalesData.Run(SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("category"));
            d.Rows.Add(new ReportDimension("product"));
            d.Columns.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "inCategory", Display = MeasureDisplay.PercentOfParentRow });
            d.Measures.Add(new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "cumulative", Display = MeasureDisplay.RunningTotal, Axis = ReportAxis.Columns });
        }));

        int tea = r.RowLabels().ToList().IndexOf("Çay");
        int total = r.GrandTotalColumn;
        Assert.Equal(150m / 206m, r.Value(tea, total, "inCategory"));

        int beverages = r.RowLabels().ToList().IndexOf("İçecek"); // 2024: 186, 2025: 20
        Assert.Equal(186m, r.Value(beverages, 0, "cumulative"));
        Assert.Equal(206m, r.Value(beverages, 1, "cumulative"));
    }

    [Fact]
    public void Top_n_with_others_merges_the_rest_correctly()
    {
        ReportResult r = SalesData.Run(SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("product") { Sort = DimensionSort.MeasureDescending, Top = 2 });
            d.Measures.Add(new ReportMeasure("customer", ReportAggregate.CountDistinct) { Name = "customers" });
        }));

        Assert.Equal(new[] { "Çay", "Çikolata", "Diğer", "Genel toplam" }, r.RowLabels());
        Assert.Equal(new decimal?[] { 150, 105, 156, 411 }, r.Column());
        Assert.Equal(ReportHeaderKind.Others, r.Rows[2].Kind);
        // Diğer = Kahve (B, -), Lokum (C), Tuz (B): farklı müşteri 2 (toplanmaz, birleşir)
        Assert.Equal(2m, r.Value(2, 0, "customers"));
    }

    [Fact]
    public void Having_filters_leaf_rows_and_drops_empty_groups()
    {
        ReportResult flat = SalesData.Run(SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("product"));
            d.Having.Add(new ReportHaving("revenue", FilterOperator.GreaterThan, [100m]));
        }));
        Assert.Equal(new[] { "Çay", "Çikolata", "Genel toplam" }, flat.RowLabels());

        ReportResult nested = SalesData.Run(SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("category"));
            d.Rows.Add(new ReportDimension("product"));
            d.Having.Add(new ReportHaving("revenue", FilterOperator.GreaterThanOrEqual, [100m]));
        }));
        Assert.Equal(new[] { "İçecek", "Çay", "Şekerleme", "Çikolata", "Genel toplam" }, nested.RowLabels());
    }

    [Fact]
    public void Filters_and_filter_expression()
    {
        decimal Total(Action<ReportDefinition> configure) => (decimal)SalesData.Run(SalesData.WithLineTotal(configure)).Value(0, 0, 0)!;

        Assert.Equal(175m, Total(d => d.Filters.Add(ReportFilter.In("country", "TR"))));
        Assert.Equal(165m, Total(d => d.Filters.Add(ReportFilter.Between("orderDate", "2025-01-01", "2025-12-31")))); // JSON'dan metin gelir
        Assert.Equal(210m, Total(d => d.FilterExpression = "Year([orderDate]) = 2024 and [quantity] >= 4"));
        Assert.Equal(105m, Total(d => d.Filters.Add(new ReportFilter("product", FilterOperator.Contains, ["çi"]))));
        Assert.Equal(20m, Total(d => d.Filters.Add(new ReportFilter("customer", FilterOperator.IsNull, []))));
        Assert.Equal(355m, Total(d => d.Filters.Add(ReportFilter.GreaterThanOrEqual("lineTotal", 40)))); // hesaplanmış alanda filtre
    }

    [Fact]
    public void Numeric_ranges_and_first_letter()
    {
        ReportResult prices = SalesData.Engine().Run(new ReportDefinition { Rows = { new ReportDimension("unitPrice") { RangeSize = 10 } } }, SalesData.DataSet);
        Assert.Equal(new[] { "0 - 10", "10 - 20", "20 - 30", "30 - 40", "Genel toplam" }, prices.RowLabels());
        Assert.Equal(new decimal?[] { 1, 4, 2, 1, 8 }, prices.Column());

        ReportResult letters = SalesData.Engine().Run(new ReportDefinition { Rows = { new ReportDimension("product") { FirstLetter = true } } }, SalesData.DataSet);
        Assert.Equal(new[] { "Ç", "K", "L", "T", "Genel toplam" }, letters.RowLabels());
        Assert.Equal(new decimal?[] { 4, 2, 1, 1, 8 }, letters.Column());
    }

    [Fact]
    public void Definition_round_trips_through_json()
    {
        ReportDefinition definition = SalesData.WithLineTotal(d =>
        {
            d.Rows.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year });
            d.Filters.Add(ReportFilter.Between("orderDate", new DateTime(2025, 1, 1), new DateTime(2025, 12, 31)));
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        string json = JsonSerializer.Serialize(definition, options);
        Assert.Contains("\"dateGrouping\":\"Year\"", json, StringComparison.Ordinal);
        Assert.Contains("\"aggregate\":\"Sum\"", json, StringComparison.Ordinal);

        ReportDefinition copy = JsonSerializer.Deserialize<ReportDefinition>(json, options)!;
        ReportResult result = SalesData.Run(copy);
        Assert.Equal(new[] { "2025", "Genel toplam" }, result.RowLabels());
        Assert.Equal(new decimal?[] { 165, 165 }, result.Column());

        string resultJson = JsonSerializer.Serialize(result, options);
        Assert.Contains("\"kind\":\"GrandTotal\"", resultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_definitions_fail_with_clear_messages()
    {
        ReportEngine engine = SalesData.Engine();
        ReportDefinitionException Fail(ReportDefinition d) => Assert.Throws<ReportDefinitionException>(() => engine.Run(d, SalesData.DataSet));

        Assert.Contains("'region' alanı yok", Fail(new ReportDefinition { Rows = { new ReportDimension("region") } }).Message, StringComparison.Ordinal);
        Assert.Contains("tarih değil", Fail(new ReportDefinition { Rows = { new ReportDimension("product") { DateGrouping = DateGrouping.Year } } }).Message, StringComparison.Ordinal);
        Assert.Contains("alan gerekli", Fail(new ReportDefinition { Measures = { new ReportMeasure(null, ReportAggregate.Sum) } }).Message, StringComparison.Ordinal);
        Assert.Contains("benzersiz", Fail(new ReportDefinition { Measures = { new ReportMeasure("quantity", ReportAggregate.Sum), new ReportMeasure("quantity", ReportAggregate.Sum) } }).Message, StringComparison.Ordinal);

        ReportDefinitionException tooBig = Assert.Throws<ReportDefinitionException>(() =>
            SalesData.Engine(maxCells: 5).Run(new ReportDefinition { Rows = { new ReportDimension("product") } }, SalesData.DataSet));
        Assert.Contains("çok büyük", tooBig.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_set_from_objects_uses_camel_case_names_and_types()
    {
        ReportDataSet data = SalesData.DataSet;

        Assert.Equal(ReportDataType.Date, data.Fields[data.IndexOf("orderDate")].Type);
        Assert.Equal(ReportDataType.Number, data.Fields[data.IndexOf("UNITPRICE")].Type);
        Assert.Equal(ReportDataType.String, data.Fields[data.IndexOf("customer")].Type);
        Assert.Equal(8, data.Rows.Count);
    }
}
