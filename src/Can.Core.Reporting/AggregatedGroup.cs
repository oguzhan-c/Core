namespace Can.Core.Reporting;

/// <summary>Veritabanında özetlenmiş bir grup (sunucu modu).</summary>
/// <param name="RowKeys">Satır gruplarının anahtarları (tanımdaki sırayla, aralık uygulanmış).</param>
/// <param name="ColumnKeys">Sütun gruplarının anahtarları.</param>
/// <param name="Rows">Gruptaki satır sayısı.</param>
/// <param name="Measures">Değer başına parça (tanımdaki sırayla).</param>
internal sealed record AggregatedGroup(object?[] RowKeys, object?[] ColumnKeys, long Rows, MeasurePartial[] Measures);

/// <param name="Count">Boş olmayan değer sayısı.</param>
/// <param name="Sum">Toplam.</param>
/// <param name="SumOfSquares">Kareler toplamı (varyans için).</param>
/// <param name="Min">En küçük.</param>
/// <param name="Max">En büyük.</param>
internal sealed record MeasurePartial(long Count, decimal? Sum, decimal? SumOfSquares, object? Min, object? Max);
