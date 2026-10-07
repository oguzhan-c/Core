using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Reporting.Tests;

/// <summary>Veritabanı satırı: SQLite decimal toplayamadığı için fiyat double (Northwind'de PostgreSQL decimal toplar).</summary>
public sealed class SaleRow
{
    public int Id { get; set; }

    public DateTime OrderDate { get; set; }

    public string Category { get; set; } = "";

    public string Product { get; set; } = "";

    public string Country { get; set; } = "";

    public double Price { get; set; }

    public int Quantity { get; set; }

    public decimal Discount { get; set; }

    public string? Customer { get; set; }
}

public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options)
{
    public DbSet<SaleRow> Sales => Set<SaleRow>();
}

/// <summary>
/// Aynı tanım veritabanında (GROUP BY) ve bellekte aynı sonucu vermeli; veritabanına çevrilemeyen tanımlar bellekte
/// hesaplanmalı.
/// </summary>
public sealed class DatabaseModeTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<SalesDbContext>(o => o.UseSqlite(_connection));
        services.AddCanReporting(o => o.Culture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))
            .AddSource("sales", sp => sp.GetRequiredService<SalesDbContext>().Sales.AsNoTracking(), o => o
                .Field("price", "Fiyat", "C2")
                .Hide("id"));
        _services = services.BuildServiceProvider();

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        SalesDbContext db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Sales.AddRange(SalesData.Rows.Select(s => new SaleRow
        {
            OrderDate = s.OrderDate,
            Category = s.Category,
            Product = s.Product,
            Country = s.Country,
            Price = (double)s.UnitPrice,
            Quantity = s.Quantity,
            Discount = s.Discount,
            Customer = s.Customer,
        }));
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<ReportResult> RunAsync(ReportDefinition definition)
    {
        definition.DataSource = "sales";
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IReportService>().RunAsync(definition, TestContext.Current.CancellationToken);
    }

    /// <summary>Aynı tanımı bellekteki veriyle çalıştırır (fiyat adı uyarlanır).</summary>
    private static ReportResult InMemory(ReportDefinition definition)
    {
        ReportDataSet data = ReportDataSet.From(SalesData.Rows.Select(s => new
        {
            s.OrderDate,
            s.Category,
            s.Product,
            s.Country,
            Price = (double)s.UnitPrice,
            s.Quantity,
            s.Discount,
            s.Customer,
        }));
        return SalesData.Engine().Run(definition, data);
    }

    private static void AssertSame(ReportResult expected, ReportResult actual)
    {
        Assert.Equal(expected.RowLabels(), actual.RowLabels());
        Assert.Equal(expected.ColumnLabels(), actual.ColumnLabels());
        for (int r = 0; r < expected.Values.Count; r++)
        {
            for (int c = 0; c < expected.Values[r].Count; c++)
            {
                object? e = expected.Values[r][c];
                object? a = actual.Values[r][c];
                if (e is decimal de && a is decimal da)
                    Assert.Equal((double)de, (double)da, 9);
                else
                    Assert.Equal(e, a);
            }
        }
    }

    private static ReportDefinition Pivot() => new()
    {
        CalculatedFields = { new ReportCalculatedField("amount", "[price] * [quantity]") },
        Filters = { ReportFilter.In("country", "TR", "DE"), ReportFilter.Between("orderDate", "2024-01-01", "2025-12-31") },
        FilterExpression = "[quantity] >= 2",
        Rows = { new ReportDimension("category") },
        Columns = { new ReportDimension("orderDate") { DateGrouping = DateGrouping.Year } },
        Measures =
        {
            new ReportMeasure("amount", ReportAggregate.Sum) { Name = "amount" },
            new ReportMeasure(null, ReportAggregate.Count) { Name = "lines" },
            new ReportMeasure("customer", ReportAggregate.Count) { Name = "withCustomer" },
            new ReportMeasure("price", ReportAggregate.Average) { Name = "avgPrice" },
            new ReportMeasure("orderDate", ReportAggregate.Max) { Name = "last" },
            new ReportMeasure("quantity", ReportAggregate.Var) { Name = "variance" },
            new ReportMeasure("amount", ReportAggregate.Sum) { Name = "share", Display = MeasureDisplay.PercentOfColumnTotal },
        },
    };

    [Fact]
    public async Task Database_mode_matches_memory_mode()
    {
        ReportResult database = await RunAsync(Pivot());

        Assert.Equal("database", database.Mode);
        AssertSame(InMemory(Pivot()), database);
        // [quantity] >= 2 → Kahve 2025 (adet 1) düşer: 7 satır
        Assert.Equal(7, database.MatchedRowCount);
        Assert.Equal(440m, database.Value(database.GrandTotalRow, database.GrandTotalColumn, "amount")); // indirimsiz: 50+40+100+60+90+40+60
    }

    [Fact]
    public async Task Date_groupings_ranges_and_top_n_in_database_mode()
    {
        ReportDefinition Definition() => new()
        {
            Rows =
            {
                new ReportDimension("orderDate") { DateGrouping = DateGrouping.YearQuarter },
                new ReportDimension("product") { Sort = DimensionSort.MeasureDescending, Top = 1 },
            },
            Columns = { new ReportDimension("price") { RangeSize = 10 }, new ReportDimension("orderDate") { DateGrouping = DateGrouping.DayOfWeek } },
            Measures = { new ReportMeasure("quantity", ReportAggregate.Sum), new ReportMeasure("price", ReportAggregate.Min) },
        };

        ReportResult database = await RunAsync(Definition());

        Assert.Equal("database", database.Mode);
        AssertSame(InMemory(Definition()), database);
    }

    [Theory]
    [InlineData(ReportAggregate.Median)]
    [InlineData(ReportAggregate.CountDistinct)]
    [InlineData(ReportAggregate.First)]
    public async Task Non_decomposable_aggregates_fall_back_to_memory(ReportAggregate aggregate)
    {
        ReportDefinition Definition() => new()
        {
            Rows = { new ReportDimension("category") },
            Measures = { new ReportMeasure("quantity", aggregate) },
        };

        ReportResult result = await RunAsync(Definition());

        Assert.Equal("memory", result.Mode);
        AssertSame(InMemory(Definition()), result);
    }

    [Fact]
    public async Task Untranslatable_parts_fall_back_to_memory()
    {
        // ISO hafta, Week() fonksiyonu ve SQLite'ın toplayamadığı decimal
        ReportDefinition[] definitions =
        [
            new() { Rows = { new ReportDimension("orderDate") { DateGrouping = DateGrouping.YearWeek } } },
            new() { FilterExpression = "Week([orderDate]) > 10" },
            new() { Measures = { new ReportMeasure("discount", ReportAggregate.Sum) } },
        ];

        foreach (ReportDefinition definition in definitions)
        {
            ReportResult result = await RunAsync(definition);
            Assert.Equal("memory", result.Mode);
        }

        ReportResult weeks = await RunAsync(new ReportDefinition { FilterExpression = "Week([orderDate]) > 10" });
        Assert.Equal(5m, weeks.Value(0, 0, 0)); // ISO hafta > 10: 2024-04-05 (14), 2024-07-20 (29), 2025-03-12 (11), 2025-05-30 ve 2025-06-01 (22)
    }

    [Fact]
    public async Task Fields_have_captions_and_hidden_fields_are_not_available()
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        IReportService reports = scope.ServiceProvider.GetRequiredService<IReportService>();
        IReportDataSource source = reports.Find("SALES")!;

        Assert.Equal("Fiyat", source.Fields.Single(f => f.Name == "price").Caption);
        Assert.Equal("C2", source.Fields.Single(f => f.Name == "price").Format);
        Assert.DoesNotContain(source.Fields, f => f.Name == "id");

        await Assert.ThrowsAsync<ReportDefinitionException>(() => RunAsync(new ReportDefinition { Rows = { new ReportDimension("id") } }));
        ReportDefinitionException missing = await Assert.ThrowsAsync<ReportDefinitionException>(() =>
            reports.RunAsync(new ReportDefinition { DataSource = "unknown" }, TestContext.Current.CancellationToken));
        Assert.Contains("veri kaynağı yok", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Memory_mode_reads_at_most_max_rows()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SalesDbContext>(o => o.UseSqlite(_connection));
        services.AddCanReporting().AddSource("sales", sp => sp.GetRequiredService<SalesDbContext>().Sales, o => o.MaxRows = 3);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        var definition = new ReportDefinition { DataSource = "sales", Measures = { new ReportMeasure("quantity", ReportAggregate.Median) } };
        ReportDefinitionException ex = await Assert.ThrowsAsync<ReportDefinitionException>(() =>
            scope.ServiceProvider.GetRequiredService<IReportService>().RunAsync(definition, TestContext.Current.CancellationToken));
        Assert.Contains("filtreyi daralt", ex.Message, StringComparison.Ordinal);
    }
}
