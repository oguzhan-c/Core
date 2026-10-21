using System.Buffers.Text;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanFx.Domain.Exceptions;

namespace CanFx.Persistence.Paging;

/// <summary>
/// İmleçli (keyset / cursor) sayfa. Toplam sayı ve sayfa numarası yoktur; istemci <see cref="NextCursor"/> ya da
/// <see cref="PreviousCursor"/>'ı bir sonraki isteğe olduğu gibi verir.
/// </summary>
public interface ICursorPage<T>
{
    IReadOnlyList<T> Items { get; }

    /// <summary>İstenen sayfa boyutu.</summary>
    int Size { get; }

    /// <summary>Sonraki sayfanın imleci; son sayfadaysa <see langword="null"/>.</summary>
    string? NextCursor { get; }

    /// <summary>Önceki sayfanın imleci; ilk sayfadaysa <see langword="null"/>.</summary>
    string? PreviousCursor { get; }

    bool HasNext { get; }

    bool HasPrevious { get; }
}

public sealed class CursorPage<T> : ICursorPage<T>
{
    public CursorPage(IReadOnlyList<T> items, int size, string? nextCursor, string? previousCursor)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        Items = items;
        Size = size;
        NextCursor = nextCursor;
        PreviousCursor = previousCursor;
    }

    public IReadOnlyList<T> Items { get; }
    public int Size { get; }
    public string? NextCursor { get; }
    public string? PreviousCursor { get; }
    public bool HasNext => NextCursor is not null;
    public bool HasPrevious => PreviousCursor is not null;
}

/// <summary>
/// İmleçli sayfalamanın sıralaması. Anahtarların birlikte <b>benzersiz</b> olması gerekir (son anahtar genelde Id);
/// aksi halde aynı değere sahip kayıtlar sayfa sınırında atlanabilir ya da tekrar edebilir. Repository
/// (<c>GetCursorPageAsync</c>) Id'yi kendiliğinden ekler.
/// </summary>
/// <remarks>
/// Anahtarlar <see langword="null"/> olamayan, karşılaştırılabilir tiplerdir (sayılar, tarih/saat, <see cref="Guid"/>,
/// <see cref="string"/>, enum). Null olabilen bir kolon için birleşik ifade kullan: <c>p =&gt; p.PublishedAt ?? p.CreatedAt</c>.
/// Veritabanında bu kolonlara (sıradaki gibi) bileşik bir index koy: sayfa hangi derinlikte olursa olsun index'ten okunur.
/// </remarks>
/// <example>
/// <code>
/// var order = CursorOrder&lt;Product&gt;.ByDescending(p =&gt; p.CreatedAt).ThenByDescending(p =&gt; p.Id);
/// ICursorPage&lt;Product&gt; page = await db.Products.Where(p =&gt; p.IsActive).ToCursorPageAsync(order, request.Cursor, 20, ct);
/// </code>
/// </example>
public sealed class CursorOrder<T>
{
    private readonly List<CursorKey> _keys;
    private string? _signature;

    private CursorOrder(List<CursorKey> keys)
    {
        _keys = keys;
    }

    internal IReadOnlyList<CursorKey> Keys => _keys;

    /// <summary>Artan sıralı ilk anahtar.</summary>
    public static CursorOrder<T> By<TKey>(Expression<Func<T, TKey>> key) => new([CursorKey.Create(key, descending: false)]);

    /// <summary>Azalan sıralı ilk anahtar.</summary>
    public static CursorOrder<T> ByDescending<TKey>(Expression<Func<T, TKey>> key) => new([CursorKey.Create(key, descending: true)]);

    /// <summary>Artan sıralı sonraki anahtar (yeni bir sıralama döner; bu nesne değişmez).</summary>
    public CursorOrder<T> ThenBy<TKey>(Expression<Func<T, TKey>> key) => new([.. _keys, CursorKey.Create(key, descending: false)]);

    /// <summary>Azalan sıralı sonraki anahtar (yeni bir sıralama döner; bu nesne değişmez).</summary>
    public CursorOrder<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> key) => new([.. _keys, CursorKey.Create(key, descending: true)]);

    /// <summary>
    /// Son anahtar <paramref name="key"/> değilse onu, son anahtarın yönüyle ekler (ör. Id ile benzersizlik).
    /// </summary>
    /// <param name="key">Benzersiz anahtar.</param>
    public CursorOrder<T> EnsureUniqueBy<TKey>(Expression<Func<T, TKey>> key)
    {
        CursorKey unique = CursorKey.Create(key, _keys[^1].Descending);
        return _keys[^1].SameSelector(unique) ? this : new CursorOrder<T>([.. _keys, unique]);
    }

    /// <summary>İmlecin bu sıralamaya ait olduğunu doğrulamak için kısa özet.</summary>
    internal string Signature => _signature ??= ComputeSignature();

    private string ComputeSignature()
    {
        var text = new StringBuilder(typeof(T).FullName);
        foreach (CursorKey key in _keys)
            text.Append('|').Append(key.Normalized()).Append(':').Append(key.Type.FullName).Append(key.Descending ? ":d" : ":a");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 6));
    }
}

/// <summary>Tek sıralama anahtarı.</summary>
internal sealed class CursorKey
{
    private Delegate? _compiled;

    private CursorKey(LambdaExpression selector, bool descending)
    {
        Selector = selector;
        Type = selector.ReturnType;
        Descending = descending;
    }

    public LambdaExpression Selector { get; }

    public Type Type { get; }

    public bool Descending { get; }

    public static CursorKey Create(LambdaExpression selector, bool descending)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Type type = selector.ReturnType;
        if (Nullable.GetUnderlyingType(type) is not null)
            throw new ArgumentException($"İmleç anahtarı null olabilen bir tip olamaz ({selector}). Birleşik ifade kullan: x => x.Kolon ?? varsayılan.", nameof(selector));
        if (!type.IsEnum && !typeof(IComparable).IsAssignableFrom(type))
            throw new ArgumentException($"İmleç anahtarı karşılaştırılabilir bir tip olmalı ({selector}: {type.Name}).", nameof(selector));
        return new CursorKey(selector, descending);
    }

    public bool SameSelector(CursorKey other) => Type == other.Type && Normalized() == other.Normalized();

    /// <summary>Parametre adından bağımsız metin (<c>p =&gt; p.Id</c> ile <c>e =&gt; e.Id</c> aynıdır).</summary>
    public string Normalized()
    {
        ParameterExpression parameter = Selector.Parameters[0];
        return new ParameterRebinder(parameter, Expression.Parameter(parameter.Type, "x")).Visit(Selector.Body).ToString();
    }

    public object? ValueOf(object item) => (_compiled ??= Selector.Compile(preferInterpretation: true)).DynamicInvoke(item);
}

/// <summary>İmleçli sayfalamanın sağlayıcıdan bağımsız kısmı: imleç, koşul ve sıralama.</summary>
public static class CursorPaging
{
    internal const string After = "a";
    internal const string Before = "b";

    /// <summary>Bellekteki ya da EF dışı bir sorguyu imleçle sayfalar (EF Core için <c>ToCursorPageAsync</c>).</summary>
    /// <param name="source">Filtrelenmiş, SIRALANMAMIŞ sorgu (sıralamayı <paramref name="order"/> belirler).</param>
    /// <param name="order">Benzersiz sıralama.</param>
    /// <param name="cursor">İstemcinin gönderdiği imleç; ilk sayfa için <see langword="null"/>.</param>
    /// <param name="size">Sayfa boyutu.</param>
    /// <exception cref="BusinessException">İmleç bozuk ya da başka bir sıralamaya ait (<c>cursor_invalid</c>, 400).</exception>
    public static ICursorPage<T> ToCursorPage<T>(this IQueryable<T> source, CursorOrder<T> order, string? cursor, int size)
    {
        CursorQuery<T> query = Prepare(source, order, cursor, size);
        return query.Complete(query.Query.ToList());
    }

    /// <summary>
    /// Sorguyu hazırlar (<c>size + 1</c> kayıt çeker); sonucu <see cref="CursorQuery{T}.Complete"/>'e ver. EF Core
    /// sağlayıcıları sorguyu asenkron çalıştırmak için kullanır.
    /// </summary>
    /// <exception cref="BusinessException">İmleç bozuk ya da başka bir sıralamaya ait (<c>cursor_invalid</c>, 400).</exception>
    public static CursorQuery<T> Prepare<T>(IQueryable<T> source, CursorOrder<T> order, string? cursor, int size)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        (bool backward, object?[]? values) = Decode(order, cursor);
        IQueryable<T> query = source;
        if (values is not null)
            query = query.Where(Seek(order, values, backward));
        query = Sort(query, order, reverse: backward).Take(size + 1);
        return new CursorQuery<T>(query, order, size, backward, values);
    }

    /// <summary>Sayfa bilgisini koruyarak elemanları dönüştürür (entity → DTO).</summary>
    public static ICursorPage<TResult> Map<TSource, TResult>(this ICursorPage<TSource> source, Func<TSource, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);
        return new CursorPage<TResult>(source.Items.Select(selector).ToList(), source.Size, source.NextCursor, source.PreviousCursor);
    }

    // ---------------------------------------------------------------- imleç

    internal static string Encode<T>(CursorOrder<T> order, string direction, object?[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("d", direction);
            writer.WriteString("s", order.Signature);
            writer.WriteStartArray("v");
            for (int i = 0; i < values.Length; i++)
                JsonSerializer.Serialize(writer, values[i], order.Keys[i].Type);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Base64Url.EncodeToString(stream.ToArray());
    }

    private static (bool Backward, object?[]? Values) Decode<T>(CursorOrder<T> order, string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return (false, null);

        try
        {
            if (cursor.Length > 4096)
                throw Invalid();
            using JsonDocument document = JsonDocument.Parse(Base64Url.DecodeFromChars(cursor));
            JsonElement root = document.RootElement;
            string? direction = root.GetProperty("d").GetString();
            JsonElement values = root.GetProperty("v");
            if (direction is not (After or Before)
                || root.GetProperty("s").GetString() != order.Signature
                || values.GetArrayLength() != order.Keys.Count)
            {
                throw Invalid();
            }

            object?[] parsed = new object?[order.Keys.Count];
            int index = 0;
            foreach (JsonElement value in values.EnumerateArray())
            {
                parsed[index] = value.Deserialize(order.Keys[index].Type) ?? throw Invalid();
                index++;
            }

            return (direction == Before, parsed);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            throw Invalid();
        }
    }

    private static BusinessException Invalid() =>
        new("Sayfa imleci geçersiz ya da bu sıralamaya ait değil; ilk sayfadan başla.") { Code = "cursor_invalid" };

    // ---------------------------------------------------------------- sorgu

    private static IQueryable<T> Sort<T>(IQueryable<T> query, CursorOrder<T> order, bool reverse)
    {
        Expression expression = query.Expression;
        for (int i = 0; i < order.Keys.Count; i++)
        {
            CursorKey key = order.Keys[i];
            bool descending = key.Descending != reverse;
            string method = (i == 0 ? "OrderBy" : "ThenBy") + (descending ? "Descending" : "");
            expression = Expression.Call(typeof(Queryable), method, [typeof(T), key.Type], expression, Expression.Quote(key.Selector));
        }

        return query.Provider.CreateQuery<T>(expression);
    }

    /// <summary>
    /// (k1, k2, ..., kn) sırasında imleçten sonrası (ya da öncesi):
    /// <c>k1 &gt; v1 OR (k1 = v1 AND k2 &gt; v2) OR ...</c>; azalan anahtarda karşılaştırma ters döner.
    /// Değerler sabit değil parametre olarak geçer (sorgu planı önbelleği her imleçte yeniden derlemez).
    /// </summary>
    private static Expression<Func<T, bool>> Seek<T>(CursorOrder<T> order, object?[] values, bool backward)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(T), "x");
        Expression? result = null;
        Expression? equalSoFar = null;

        for (int i = 0; i < order.Keys.Count; i++)
        {
            CursorKey key = order.Keys[i];
            Expression member = new ParameterRebinder(key.Selector.Parameters[0], parameter).Visit(key.Selector.Body);
            Expression value = Parameterize(values[i], key.Type);

            bool greater = key.Descending == backward; // artan + ileri ya da azalan + geri: büyük olanlar
            Expression comparison = Compare(member, value, greater);
            Expression term = equalSoFar is null ? comparison : Expression.AndAlso(equalSoFar, comparison);
            result = result is null ? term : Expression.OrElse(result, term);

            Expression equal = Equal(member, value);
            equalSoFar = equalSoFar is null ? equal : Expression.AndAlso(equalSoFar, equal);
        }

        return Expression.Lambda<Func<T, bool>>(result!, parameter);
    }

    private static Expression Compare(Expression left, Expression right, bool greater)
    {
        Type type = left.Type;
        if (type.IsEnum)
        {
            Type underlying = Enum.GetUnderlyingType(type);
            return Compare(Expression.Convert(left, underlying), Expression.Convert(right, underlying), greater);
        }

        try
        {
            return greater ? Expression.GreaterThan(left, right) : Expression.LessThan(left, right);
        }
        catch (InvalidOperationException)
        {
            // Karşılaştırma işleci olmayan tipler (string...): a.CompareTo(b) > 0
            MethodInfo compareTo = type.GetMethod(nameof(IComparable.CompareTo), [type])
                ?? throw new ArgumentException($"'{type.Name}' imleç anahtarı olarak karşılaştırılamıyor.");
            Expression call = Expression.Call(left, compareTo, right);
            Expression zero = Expression.Constant(0);
            return greater ? Expression.GreaterThan(call, zero) : Expression.LessThan(call, zero);
        }
    }

    private static Expression Equal(Expression left, Expression right)
    {
        try
        {
            return Expression.Equal(left, right);
        }
        catch (InvalidOperationException)
        {
            MethodInfo compareTo = left.Type.GetMethod(nameof(IComparable.CompareTo), [left.Type])!;
            return Expression.Equal(Expression.Call(left, compareTo, right), Expression.Constant(0));
        }
    }

    private static MemberExpression Parameterize(object? value, Type type)
    {
        object holder = Activator.CreateInstance(typeof(ValueHolder<>).MakeGenericType(type), value)!;
        return Expression.Property(Expression.Constant(holder), nameof(ValueHolder<int>.Value));
    }

    private sealed class ValueHolder<TValue>(TValue value)
    {
        public TValue Value { get; } = value;
    }

}

/// <summary>Hazırlanmış imleç sorgusu (<see cref="CursorPaging.Prepare{T}"/>).</summary>
public sealed class CursorQuery<T>
{
    private readonly CursorOrder<T> _order;
    private readonly int _size;
    private readonly bool _backward;
    private readonly object?[]? _cursorValues;

    internal CursorQuery(IQueryable<T> query, CursorOrder<T> order, int size, bool backward, object?[]? cursorValues)
    {
        Query = query;
        _order = order;
        _size = size;
        _backward = backward;
        _cursorValues = cursorValues;
    }

    /// <summary>Çalıştırılacak sorgu (en çok <c>size + 1</c> kayıt).</summary>
    public IQueryable<T> Query { get; }

    /// <summary>Çekilen kayıtlardan sayfayı ve imleçleri üretir.</summary>
    /// <param name="fetched">Sorgunun sonucu (değiştirilir).</param>
    public ICursorPage<T> Complete(List<T> fetched)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        bool more = fetched.Count > _size; // fazladan bir kayıt geldiyse o yönde devamı var
        if (more)
            fetched.RemoveAt(fetched.Count - 1);
        if (_backward)
            fetched.Reverse(); // geri giderken ters sırayla çekildi

        object?[]? first = fetched.Count > 0 ? ValuesOf(fetched[0]) : _cursorValues;
        object?[]? last = fetched.Count > 0 ? ValuesOf(fetched[^1]) : _cursorValues;

        string? next, previous;
        if (_backward)
        {
            previous = more ? CursorPaging.Encode(_order, CursorPaging.Before, first!) : null;
            next = CursorPaging.Encode(_order, CursorPaging.After, last!); // sonraki sayfadan gelindi
        }
        else
        {
            next = more ? CursorPaging.Encode(_order, CursorPaging.After, last!) : null;
            previous = _cursorValues is null ? null : CursorPaging.Encode(_order, CursorPaging.Before, first!);
        }

        return new CursorPage<T>(fetched, _size, next, previous);
    }

    private object?[] ValuesOf(T item) => _order.Keys.Select(k => k.ValueOf(item!)).ToArray();
}

internal sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
{
    protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
}
