using System.Text.Json.Serialization;

namespace Can.Core.Reporting;

/// <summary>
/// Bir raporun tanımı: hangi veriden, hangi filtreyle, neye göre gruplanıp neyin hesaplanacağı. JSON olarak saklanabilir
/// ve arayüzden kurulabilir (rapor tasarımcısı bunu üretir).
/// </summary>
/// <example>
/// <code>
/// new ReportDefinition
/// {
///     CalculatedFields = { new("lineTotal", "[unitPrice] * [quantity] * (1 - [discount])") },
///     Filters = { ReportFilter.Between("orderDate", new DateTime(2024, 1, 1), new DateTime(2024, 12, 31)) },
///     Rows = { new ReportDimension("category"), new ReportDimension("product") { Top = 5 } },
///     Columns = { new ReportDimension("orderDate") { DateGrouping = DateGrouping.Quarter } },
///     Measures =
///     {
///         new ReportMeasure("lineTotal", ReportAggregate.Sum) { Caption = "Ciro", Format = "C2" },
///         new ReportMeasure("lineTotal", ReportAggregate.Sum) { Name = "share", Display = MeasureDisplay.PercentOfColumnTotal },
///     },
/// };
/// </code>
/// </example>
public sealed class ReportDefinition
{
    /// <summary>Veri kaynağının adı (kayıtlı kaynaklardan biri).</summary>
    public string? DataSource { get; set; }

    public string? Title { get; set; }

    /// <summary>Satır başına hesaplanan yeni alanlar; sırayla hesaplanır, öncekilere başvurabilir.</summary>
    public List<ReportCalculatedField> CalculatedFields { get; set; } = [];

    /// <summary>Gruplamadan önce satırları daraltır (hepsi sağlanmalı).</summary>
    public List<ReportFilter> Filters { get; set; } = [];

    /// <summary>İfade ile filtre (ör. <c>Year([orderDate]) = 2024 and [country] != 'USA'</c>). Yalnızca bellek içinde.</summary>
    public string? FilterExpression { get; set; }

    /// <summary>Satır gruplama düzeyleri (dıştan içe).</summary>
    public List<ReportDimension> Rows { get; set; } = [];

    /// <summary>Sütun gruplama düzeyleri (çapraz tablo / pivot).</summary>
    public List<ReportDimension> Columns { get; set; } = [];

    /// <summary>Hesaplanacak değerler.</summary>
    public List<ReportMeasure> Measures { get; set; } = [];

    /// <summary>Gruplamadan SONRA en alt satırları daraltır (ör. cirosu 1000'den büyük ürünler).</summary>
    public List<ReportHaving> Having { get; set; } = [];

    /// <summary>Ara toplamlar (her grup düzeyi için). Kapalıysa yalnızca en alt satırlar ve genel toplam.</summary>
    public bool ShowSubtotals { get; set; } = true;

    public bool ShowRowGrandTotal { get; set; } = true;

    public bool ShowColumnGrandTotal { get; set; } = true;
}

/// <param name="Name">Yeni alanın adı.</param>
/// <param name="Expression">İfade (<see cref="ReportExpression"/>): <c>[unitPrice] * [quantity]</c>.</param>
public sealed record ReportCalculatedField(string Name, string Expression)
{
    public string? Caption { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DateGrouping>))]
public enum DateGrouping
{
    /// <summary>Tarih olduğu gibi (saat dahil).</summary>
    None,

    /// <summary>Yalnızca gün (saat atılır).</summary>
    Date,
    Year,

    /// <summary>Yıl + çeyrek (2024 Ç1).</summary>
    YearQuarter,

    /// <summary>Yıl + ay (Mart 2024).</summary>
    YearMonth,

    /// <summary>Yıl + ISO hafta (2024 H12).</summary>
    YearWeek,

    /// <summary>Yıldan bağımsız çeyrek (1-4): mevsimsellik.</summary>
    Quarter,

    /// <summary>Yıldan bağımsız ay (1-12).</summary>
    Month,

    /// <summary>Haftanın günü (Pazartesi=1).</summary>
    DayOfWeek,

    /// <summary>Ayın günü (1-31).</summary>
    DayOfMonth,

    /// <summary>Günün saati (0-23).</summary>
    Hour,
}

[JsonConverter(typeof(JsonStringEnumConverter<DimensionSort>))]
public enum DimensionSort
{
    /// <summary>Anahtara göre artan (tarih/sayı/alfabetik).</summary>
    KeyAscending,
    KeyDescending,

    /// <summary><see cref="ReportDimension.SortByMeasure"/>'in genel toplamına göre artan.</summary>
    MeasureAscending,
    MeasureDescending,
}

/// <summary>Bir gruplama düzeyi.</summary>
/// <param name="Field">Alan adı.</param>
public sealed record ReportDimension(string Field)
{
    public string? Caption { get; init; }

    /// <summary>Tarih alanlarında aralık.</summary>
    public DateGrouping DateGrouping { get; init; } = DateGrouping.None;

    /// <summary>Sayı alanlarında aralık genişliği (ör. 10 → 0-10, 10-20 ...).</summary>
    public decimal? RangeSize { get; init; }

    /// <summary>Metin alanlarında ilk harfe göre grupla.</summary>
    public bool FirstLetter { get; init; }

    public DimensionSort Sort { get; init; } = DimensionSort.KeyAscending;

    /// <summary>Sıralama değerin adı (<see cref="ReportMeasure.Name"/>); boşsa ilk değer.</summary>
    public string? SortByMeasure { get; init; }

    /// <summary>Her üst grubun altında yalnızca ilk N (sıralamaya göre); gerisi <see cref="ShowOthers"/> ise "Diğer"de toplanır.</summary>
    public int? Top { get; init; }

    public bool ShowOthers { get; init; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter<ReportAggregate>))]
public enum ReportAggregate
{
    Sum,

    /// <summary>Alan verilmişse boş olmayan değer sayısı, verilmemişse satır sayısı.</summary>
    Count,

    /// <summary>Farklı değer sayısı.</summary>
    CountDistinct,
    Average,
    Min,
    Max,

    /// <summary>Ortanca (çift sayıda değerde ortadaki ikisinin ortalaması).</summary>
    Median,

    /// <summary>Yüzdelik (<see cref="ReportMeasure.Percentile"/>: 0-1, ör. 0.9 = 90. yüzdelik; doğrusal aradeğerleme).</summary>
    Percentile,

    /// <summary>Örneklem standart sapması (n-1).</summary>
    StdDev,

    /// <summary>Kitle standart sapması (n).</summary>
    StdDevP,

    /// <summary>Örneklem varyansı (n-1).</summary>
    Var,

    /// <summary>Kitle varyansı (n).</summary>
    VarP,

    /// <summary>Gruptaki ilk değer (veri sırasıyla).</summary>
    First,

    /// <summary>Gruptaki son değer.</summary>
    Last,
}

/// <summary>Hesaplanan değerin nasıl gösterileceği (DevExpress'teki "summary display type" karşılığı).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MeasureDisplay>))]
public enum MeasureDisplay
{
    Value,

    /// <summary>Satırın genel toplamına oranı (0-1).</summary>
    PercentOfRowTotal,

    /// <summary>Sütunun genel toplamına oranı.</summary>
    PercentOfColumnTotal,

    /// <summary>Raporun genel toplamına oranı.</summary>
    PercentOfGrandTotal,

    /// <summary>Üst satır grubuna oranı (ör. ürünün kategorisi içindeki payı).</summary>
    PercentOfParentRow,

    /// <summary>Üst sütun grubuna oranı (ör. çeyreğin yıl içindeki payı).</summary>
    PercentOfParentColumn,

    /// <summary>Kümülatif toplam (<see cref="ReportMeasure.Axis"/> boyunca, aynı üst grup içinde).</summary>
    RunningTotal,

    /// <summary>Bir öncekine göre fark (ör. önceki aya göre).</summary>
    DifferenceFromPrevious,

    /// <summary>Bir öncekine göre yüzde değişim (0.1 = %10 artış).</summary>
    PercentDifferenceFromPrevious,

    /// <summary>Kardeşler arasında sıra (en büyük = 1).</summary>
    Rank,
}

[JsonConverter(typeof(JsonStringEnumConverter<ReportAxis>))]
public enum ReportAxis
{
    Rows,
    Columns,
}

/// <summary>Hesaplanacak değer.</summary>
/// <param name="Field">Alan adı (Count'ta boş bırakılırsa satır sayılır).</param>
/// <param name="Aggregate">Özet fonksiyonu.</param>
public sealed record ReportMeasure(string? Field, ReportAggregate Aggregate)
{
    /// <summary>Sonuçtaki adı; boşsa <c>{aggregate}_{field}</c>.</summary>
    public string? Name { get; init; }

    public string? Caption { get; init; }

    /// <summary>Görüntü biçimi (.NET biçim dizesi: <c>N2</c>, <c>C2</c>, <c>P1</c>).</summary>
    public string? Format { get; init; }

    public MeasureDisplay Display { get; init; } = MeasureDisplay.Value;

    /// <summary>Kümülatif / fark / sıra hangi eksende (varsayılan: satırlar, ör. aylar alt alta).</summary>
    public ReportAxis Axis { get; init; } = ReportAxis.Rows;

    /// <summary><see cref="ReportAggregate.Percentile"/> için 0-1.</summary>
    public double? Percentile { get; init; }

    [JsonIgnore]
    public string EffectiveName => Name ?? $"{Aggregate.ToString().ToLowerInvariant()}_{Field ?? "rows"}";
}

[JsonConverter(typeof(JsonStringEnumConverter<FilterOperator>))]
public enum FilterOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,

    /// <summary>İki değer arasında (iki uç dahil).</summary>
    Between,
    In,
    NotIn,
    Contains,
    StartsWith,
    IsNull,
    IsNotNull,
}

/// <summary>Satır filtresi.</summary>
public sealed record ReportFilter(string Field, FilterOperator Operator, IReadOnlyList<object?> Values)
{
    public static ReportFilter Equal(string field, object? value) => new(field, FilterOperator.Equal, [value]);

    public static ReportFilter In(string field, params object?[] values) => new(field, FilterOperator.In, values);

    public static ReportFilter Between(string field, object from, object to) => new(field, FilterOperator.Between, [from, to]);

    public static ReportFilter GreaterThanOrEqual(string field, object value) => new(field, FilterOperator.GreaterThanOrEqual, [value]);
}

/// <summary>Gruplama sonrası filtre: en alt satırlarda, sütun genel toplamındaki değere göre.</summary>
/// <param name="Measure">Değerin adı.</param>
/// <param name="Operator">Karşılaştırma (Equal ... Between).</param>
/// <param name="Values">Karşılaştırılacak değer(ler).</param>
public sealed record ReportHaving(string Measure, FilterOperator Operator, IReadOnlyList<object?> Values);
