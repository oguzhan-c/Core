namespace Can.Core.Persistence.Paging;

/// <summary>Sayfalanmış sonuç. Sayfa numarası (<see cref="Index"/>) 0'dan başlar.</summary>
public interface IPaginate<T>
{
    /// <summary>İlk sayfanın numarası (genelde 0). Arayüz 1'den sayıyorsa 1 verilebilir.</summary>
    int From { get; }

    /// <summary>Bu sayfanın numarası.</summary>
    int Index { get; }

    /// <summary>Sayfa başına kayıt sayısı.</summary>
    int Size { get; }

    /// <summary>Filtreye uyan toplam kayıt sayısı.</summary>
    int Count { get; }

    /// <summary>Toplam sayfa sayısı.</summary>
    int Pages { get; }

    IReadOnlyList<T> Items { get; }

    bool HasPrevious { get; }

    bool HasNext { get; }
}

public sealed class Paginate<T> : IPaginate<T>
{
    public Paginate(IReadOnlyList<T> items, int index, int size, int count, int from = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        PagingGuard.Validate(index, size, from);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Items = items;
        Index = index;
        Size = size;
        Count = count;
        From = from;
        Pages = (int)Math.Ceiling(count / (double)size);
    }

    public int From { get; }
    public int Index { get; }
    public int Size { get; }
    public int Count { get; }
    public int Pages { get; }
    public IReadOnlyList<T> Items { get; }
    public bool HasPrevious => Index - From > 0;
    public bool HasNext => Index - From + 1 < Pages;

    public static Paginate<T> Empty(int index = 0, int size = 10, int from = 0) => new([], index, size, 0, from);
}

public static class PaginateExtensions
{
    /// <summary>
    /// Sayfa bilgisini koruyarak elemanları dönüştürür (ör. entity → DTO):
    /// <c>page.Map(p =&gt; mapper.Map&lt;ProductDto&gt;(p))</c>.
    /// </summary>
    public static IPaginate<TResult> Map<TSource, TResult>(this IPaginate<TSource> source, Func<TSource, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        return new Paginate<TResult>(source.Items.Select(selector).ToList(), source.Index, source.Size, source.Count, source.From);
    }

    /// <summary>Bellekteki bir koleksiyonu sayfalar.</summary>
    public static IPaginate<T> ToPaginate<T>(this IEnumerable<T> source, int index, int size, int from = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        PagingGuard.Validate(index, size, from);

        IReadOnlyCollection<T> all = source as IReadOnlyCollection<T> ?? source.ToList();
        List<T> items = all.Skip((index - from) * size).Take(size).ToList();

        return new Paginate<T>(items, index, size, all.Count, from);
    }
}

public static class PagingGuard
{
    public static void Validate(int index, int size, int from)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ArgumentOutOfRangeException.ThrowIfNegative(from);

        if (index < from)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Sayfa numarası ({index}) From değerinden ({from}) küçük olamaz.");
    }
}
