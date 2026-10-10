namespace Can.Core.Reporting.Tests;

public sealed class ReportDefinitionValidationTests
{
    private static IReadOnlyList<ReportField> Fields => SalesData.DataSet.Fields;

    [Fact]
    public void Valid_definition_has_no_errors()
    {
        ReportDefinition definition = SalesData.WithLineTotal(d =>
        {
            d.Filters.Add(ReportFilter.Between("orderDate", new DateTime(2024, 1, 1), new DateTime(2025, 12, 31)));
            d.FilterExpression = "[lineTotal] > 10";
            d.Rows.Add(new ReportDimension("category") { SortByMeasure = "revenue", Sort = DimensionSort.MeasureDescending, Top = 2 });
            d.Columns.Add(new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year });
            d.Having.Add(new ReportHaving("revenue", FilterOperator.GreaterThan, [50m]));
        });

        Assert.Empty(definition.Validate(Fields));
    }

    [Fact]
    public void All_errors_are_collected()
    {
        var definition = new ReportDefinition
        {
            CalculatedFields = { new ReportCalculatedField("category", "1"), new ReportCalculatedField("bad", "[unitPrice] *") },
            Filters = { ReportFilter.Equal("missing", 1), new ReportFilter("quantity", FilterOperator.Between, [1]) },
            FilterExpression = "[nope] = 1",
            Rows = { new ReportDimension("country") { DateGrouping = DateGrouping.Year }, new ReportDimension("ghost") },
            Columns = { new ReportDimension("quantity") { RangeSize = 0 }, new ReportDimension("product") { SortByMeasure = "x", Top = 0 } },
            Measures =
            {
                new ReportMeasure(null, ReportAggregate.Sum),
                new ReportMeasure("quantity", ReportAggregate.Percentile) { Name = "p", Percentile = 1.5 },
                new ReportMeasure("quantity", ReportAggregate.Sum) { Name = "p" },
                new ReportMeasure("unknown", ReportAggregate.Max),
            },
            Having = { new ReportHaving("zzz", FilterOperator.Contains, ["a"]) },
        };

        IReadOnlyList<string> errors = definition.Validate(Fields);

        string[] expected =
        [
            "'category'",          // hesaplanmış alan adı zaten var
            "'bad' hesaplanmış",   // ifade hatalı
            "'missing' alanı yok",
            "2 değer ister",
            "Filtre ifadesi",
            "'p' değer adı",
            "'Sum' için alan gerekli",
            "'unknown' alanı yok",
            "Percentile 0 ile 1",
            "'country' tarih değil",
            "'ghost' alanı yok",
            "aralık genişliği",
            "'x' yok",
            "Top pozitif",
            "'zzz' değeri yok",
            "Contains kullanılamaz",
        ];
        foreach (string text in expected)
            Assert.Contains(errors, e => e.Contains(text, StringComparison.Ordinal));
        Assert.Equal(expected.Length, errors.Count);
    }

    [Fact]
    public void Having_without_row_groups_is_rejected()
    {
        ReportDefinition definition = SalesData.WithLineTotal(d => d.Having.Add(new ReportHaving("revenue", FilterOperator.GreaterThan, [1m])));

        string error = Assert.Single(definition.Validate(Fields));
        Assert.Contains("satır gruplaması", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_valid_throws_with_every_error()
    {
        var definition = new ReportDefinition { Rows = { new ReportDimension("a") }, Columns = { new ReportDimension("b") } };

        ReportDefinitionException ex = Assert.Throws<ReportDefinitionException>(() => definition.EnsureValid(Fields));

        Assert.Equal(2, ex.Errors.Count);
        Assert.Contains("'a'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'b'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Denetim, motorun reddettiği her tanımı da reddetmeli (tasarımcı çalıştırmadan hata gösterebilsin).</summary>
    [Theory]
    [MemberData(nameof(EngineRejects))]
    public void Validation_agrees_with_the_engine(string name, ReportDefinition definition)
    {
        Assert.NotEmpty(definition.Validate(Fields));
        Assert.Throws<ReportDefinitionException>(() => SalesData.Run(definition));
        Assert.NotNull(name);
    }

    public static TheoryData<string, ReportDefinition> EngineRejects => new()
    {
        { "unknown dimension", new ReportDefinition { Rows = { new ReportDimension("nope") } } },
        { "unknown measure field", new ReportDefinition { Measures = { new ReportMeasure("nope", ReportAggregate.Sum) } } },
        { "duplicate measure", new ReportDefinition { Measures = { new ReportMeasure("quantity", ReportAggregate.Sum), new ReportMeasure("quantity", ReportAggregate.Sum) } } },
        { "percentile range", new ReportDefinition { Measures = { new ReportMeasure("quantity", ReportAggregate.Percentile) { Percentile = 2 } } } },
        { "field-less sum", new ReportDefinition { Measures = { new ReportMeasure(null, ReportAggregate.Sum) } } },
        { "bad expression", new ReportDefinition { FilterExpression = "[quantity] >" } },
        { "date grouping on text", new ReportDefinition { Rows = { new ReportDimension("country") { DateGrouping = DateGrouping.Month } } } },
        { "between with one value", new ReportDefinition { Filters = { new ReportFilter("quantity", FilterOperator.Between, [1]) } } },
        { "unknown sort measure", new ReportDefinition { Rows = { new ReportDimension("category") { SortByMeasure = "nope" } } } },
    };
}
