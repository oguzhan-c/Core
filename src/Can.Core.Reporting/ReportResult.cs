using System.Text.Json.Serialization;

namespace Can.Core.Reporting;

[JsonConverter(typeof(JsonStringEnumConverter<ReportHeaderKind>))]
public enum ReportHeaderKind
{
    /// <summary>En alt düzey.</summary>
    Item,

    /// <summary>Alt düzeyleri olan grup; değerleri ara toplamdır.</summary>
    Group,

    /// <summary>Top N dışında kalanların toplamı.</summary>
    Others,

    /// <summary>Genel toplam.</summary>
    GrandTotal,
}

/// <summary>Satır ya da sütun başlığı.</summary>
/// <param name="Level">Derinlik: 0 genel toplam, 1 en dış düzey ...</param>
/// <param name="Kind">Tür.</param>
/// <param name="Keys">Üstten bu düzeye anahtarlar (gruplama değerleri).</param>
/// <param name="Labels">Anahtarların görünen metinleri.</param>
public sealed record ReportHeader(int Level, ReportHeaderKind Kind, IReadOnlyList<object?> Keys, IReadOnlyList<string> Labels)
{
    /// <summary>Bu düzeyin görünen metni.</summary>
    public string Label => Labels.Count == 0 ? string.Empty : Labels[^1];
}

/// <param name="Field">Alan.</param>
/// <param name="Caption">Başlık.</param>
/// <param name="DateGrouping">Tarih aralığı.</param>
public sealed record ReportResultDimension(string Field, string Caption, DateGrouping DateGrouping);

/// <param name="Name">Değerin adı.</param>
/// <param name="Caption">Başlık.</param>
/// <param name="Aggregate">Özet fonksiyonu.</param>
/// <param name="Display">Gösterim.</param>
/// <param name="Format">Biçim (yüzde gösterimlerinde varsayılan <c>P1</c>).</param>
public sealed record ReportResultMeasure(string Name, string Caption, ReportAggregate Aggregate, MeasureDisplay Display, string? Format);

/// <summary>
/// Rapor sonucu: satır başlıkları × sütun başlıkları × değerler. <see cref="Values"/>[satır][sütun × değer sayısı + değer].
/// Satırlarda grup başlığı alt satırlarından ÖNCE, sütunlarda ara toplam sütunu alt sütunlarından SONRA gelir;
/// genel toplamlar en sonda.
/// </summary>
public sealed record ReportResult(
    string? Title,
    IReadOnlyList<ReportResultDimension> RowDimensions,
    IReadOnlyList<ReportResultDimension> ColumnDimensions,
    IReadOnlyList<ReportResultMeasure> Measures,
    IReadOnlyList<ReportHeader> Rows,
    IReadOnlyList<ReportHeader> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Values,
    long SourceRowCount,
    long MatchedRowCount)
{
    public object? Value(int row, int column, int measure) => Values[row][(column * Measures.Count) + measure];

    /// <summary>Adı verilen değerin hücresi.</summary>
    public object? Value(int row, int column, string measure)
    {
        int index = Measures.ToList().FindIndex(m => m.Name == measure);
        return index < 0 ? throw new ArgumentException($"'{measure}' değeri yok.", nameof(measure)) : Value(row, column, index);
    }

    /// <summary>Genel toplam satırı/sütunu (yoksa -1).</summary>
    [JsonIgnore]
    public int GrandTotalRow => Rows.ToList().FindIndex(r => r.Kind == ReportHeaderKind.GrandTotal);

    [JsonIgnore]
    public int GrandTotalColumn => Columns.ToList().FindIndex(c => c.Kind == ReportHeaderKind.GrandTotal);
}
