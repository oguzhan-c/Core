namespace Can.Core.Search;

/// <summary>Arama dizinine konan belge. <see cref="Id"/> dizindeki kimliğidir (genelde entity'nin Id'si).</summary>
public interface ISearchDocument
{
    string Id { get; }
}

/// <summary>Metindeki kelimelerin hepsi mi (<see cref="All"/>) yoksa herhangi biri mi (<see cref="Any"/>) eşleşmeli.</summary>
public enum SearchOperator
{
    Any = 0,
    All = 1,
}

/// <summary>
/// Arama isteği. Alan adları belgenin JSON hâlindeki adlarıdır (varsayılan camelCase: <c>unitPrice</c>,
/// iç içe alanlar noktayla: <c>category.name</c>).
/// </summary>
/// <example>
/// <code>
/// var query = new SearchQuery
/// {
///     Text = "çay",
///     Fields = { "name^3", "description" },          // ^3: addaki eşleşme 3 kat önemli
///     Filters = { SearchFilter.Equal("discontinued", false), SearchFilter.Range("unitPrice", lte: 50) },
///     Sort = { SearchSort.Desc("unitPrice") },
///     Highlight = { "name" },
///     Aggregations = { SearchAggregation.Terms("categories", "categoryName") },
///     Page = 0,
///     Size = 20,
/// };
/// </code>
/// </example>
public sealed class SearchQuery
{
    public const int MaxSize = 1000;

    /// <summary>
    /// Sayfalamayla ulaşılabilecek en derin sonuç (<c>(Page + 1) × Size</c>). Elasticsearch'ün varsayılan
    /// <c>index.max_result_window</c> sınırıdır; daha derini için filtreyi daralt.
    /// </summary>
    public const int MaxResultWindow = 10_000;

    /// <summary>Aranan metin. Boşsa yalnızca filtreler uygulanır (tüm belgeler).</summary>
    public string? Text { get; set; }

    /// <summary>Metnin aranacağı alanlar; <c>alan^2</c> ile ağırlık verilir. Boşsa tüm metin alanları.</summary>
    public IList<string> Fields { get; } = [];

    public SearchOperator Operator { get; set; } = SearchOperator.All;

    /// <summary>Yazım hatalarına tolerans (<c>cikolata</c> → <c>çikolata</c>). Varsayılan açık.</summary>
    public bool Fuzzy { get; set; } = true;

    /// <summary>Son kelime önek olarak eşleşir (yazarken arama: <c>çik</c> → <c>çikolata</c>).</summary>
    public bool Prefix { get; set; }

    /// <summary>Sonuçları daraltır; skoru etkilemez. Hepsi sağlanmalı (VE).</summary>
    public IList<SearchFilter> Filters { get; } = [];

    /// <summary>Boşsa metin varken alaka skoruna göre sıralanır.</summary>
    public IList<SearchSort> Sort { get; } = [];

    /// <summary>Eşleşen kısmı işaretlenecek alanlar (<c>&lt;em&gt;</c> ile).</summary>
    public IList<string> Highlight { get; } = [];

    public IList<SearchAggregation> Aggregations { get; } = [];

    /// <summary>Sayfa numarası (0'dan başlar).</summary>
    public int Page { get; set; }

    public int Size { get; set; } = 20;

    internal void Validate()
    {
        if (Page < 0)
            throw new ArgumentOutOfRangeException(nameof(Page), Page, "Sayfa 0 ya da büyük olmalı.");
        if (Size is < 0 or > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(Size), Size, $"Sayfa boyutu 0-{MaxSize} arasında olmalı.");
        if ((Page + 1L) * Size > MaxResultWindow)
            throw new ArgumentOutOfRangeException(nameof(Page), Page, $"En fazla ilk {MaxResultWindow} sonuca sayfalanabilir; aramayı daralt.");
    }
}

public enum SearchFilterKind
{
    Equal,
    In,
    Range,
    Exists,
}

/// <summary>Alan filtresi. <see cref="Not"/> ile tersine çevrilir.</summary>
public sealed record SearchFilter
{
    private SearchFilter(SearchFilterKind kind, string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        Kind = kind;
        Field = field;
    }

    public SearchFilterKind Kind { get; }

    public string Field { get; }

    /// <summary><see cref="SearchFilterKind.Equal"/> için tek, <see cref="SearchFilterKind.In"/> için birden çok değer.</summary>
    public IReadOnlyList<object> Values { get; private init; } = [];

    public object? GreaterThanOrEqual { get; private init; }

    public object? GreaterThan { get; private init; }

    public object? LessThanOrEqual { get; private init; }

    public object? LessThan { get; private init; }

    public bool Negated { get; private init; }

    /// <summary>Alan tam olarak bu değer (metin alanlarında keyword alanı kullan: <c>category.keyword</c>).</summary>
    public static SearchFilter Equal(string field, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SearchFilter(SearchFilterKind.Equal, field) { Values = [value] };
    }

    /// <summary>Alan bu değerlerden biri.</summary>
    public static SearchFilter In(string field, IEnumerable<object> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        object[] list = values.ToArray();
        if (list.Length == 0)
            throw new ArgumentException("En az bir değer gerekli.", nameof(values));
        return new SearchFilter(SearchFilterKind.In, field) { Values = list };
    }

    /// <summary>Aralık (sayı, tarih, metin). Verilmeyen uç sınırsızdır.</summary>
    public static SearchFilter Range(string field, object? gte = null, object? lte = null, object? gt = null, object? lt = null)
    {
        if (gte is null && lte is null && gt is null && lt is null)
            throw new ArgumentException("En az bir sınır gerekli.", nameof(field));
        return new SearchFilter(SearchFilterKind.Range, field)
        {
            GreaterThanOrEqual = gte,
            LessThanOrEqual = lte,
            GreaterThan = gt,
            LessThan = lt,
        };
    }

    /// <summary>Alan var ve boş değil.</summary>
    public static SearchFilter Exists(string field) => new(SearchFilterKind.Exists, field);

    public SearchFilter Not() => this with { Negated = !Negated };
}

public sealed record SearchSort(string Field, bool Descending = false)
{
    public static SearchSort Asc(string field) => new(field);

    public static SearchSort Desc(string field) => new(field, true);
}

public enum SearchAggregationKind
{
    /// <summary>En sık değerler ve sayıları (kategori, marka ... yüzeyleri).</summary>
    Terms,

    /// <summary>En küçük, en büyük, ortalama, toplam.</summary>
    Stats,
}

public sealed record SearchAggregation(string Name, SearchAggregationKind Kind, string Field, int Size = 10)
{
    public static SearchAggregation Terms(string name, string field, int size = 10) => new(name, SearchAggregationKind.Terms, field, size);

    public static SearchAggregation Stats(string name, string field) => new(name, SearchAggregationKind.Stats, field);
}

// ---------------------------------------------------------------- sonuçlar

public sealed record SearchHit<TDocument>(TDocument Document, double? Score, IReadOnlyDictionary<string, IReadOnlyList<string>> Highlights);

public sealed record AggregationBucket(string Key, long Count);

public sealed record AggregationStats(long Count, double? Min, double? Max, double? Average, double Sum);

public sealed record AggregationResult(IReadOnlyList<AggregationBucket> Buckets, AggregationStats? Stats);

public sealed record SearchResult<TDocument>(
    IReadOnlyList<SearchHit<TDocument>> Hits,
    long Total,
    int Page,
    int Size,
    IReadOnlyDictionary<string, AggregationResult> Aggregations)
{
    public IEnumerable<TDocument> Documents => Hits.Select(h => h.Document);

    public int TotalPages => Size == 0 ? 0 : (int)Math.Ceiling(Total / (double)Size);
}

/// <summary>Toplu yazma sonucu: başarısız belgeler (kimlik + neden).</summary>
public sealed record SearchBulkResult(int Succeeded, IReadOnlyList<SearchBulkFailure> Failures)
{
    public bool HasFailures => Failures.Count > 0;
}

public sealed record SearchBulkFailure(string Id, string Reason);

/// <summary>Arama motorunun döndürdüğü hata (bağlantı, geçersiz sorgu, eşleme hatası ...).</summary>
public sealed class SearchException : Exception
{
    public SearchException() { }

    public SearchException(string message)
        : base(message) { }

    public SearchException(string message, Exception innerException)
        : base(message, innerException) { }

    public int? StatusCode { get; init; }

    public string? ErrorType { get; init; }
}
