using System.Linq.Expressions;
using System.Reflection;

namespace CanFx.FakeData;

/// <summary>Bir kaydın o ana kadar üretilmiş değerleri (sonraki kural öncekine bakabilir: Slug ← Name).</summary>
public sealed class FakeRow
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public FakeRow(int index) => Index = index;

    /// <summary>Kaydın sırası (0'dan).</summary>
    public int Index { get; }

    public IReadOnlyDictionary<string, object?> Values => _values;

    public object? this[string member] =>
        _values.TryGetValue(member, out object? value)
            ? value
            : throw new KeyNotFoundException($"'{member}' henüz üretilmedi: kuralı ondan sonra tanımla.");

    public T Get<T>(string member) => (T)this[member]!;

    /// <summary>Alanın değerini yazar (canfxgen kendi sırasıyla doldururken).</summary>
    public void Set(string member, object? value) => _values[member] = value;
}

/// <summary>
/// Kural tabanlı nesne üretimi. Her alanın değeri kendi tohumundan gelir (<c>entity, sıra, alan</c>): kural eklemek ya da
/// sırasını değiştirmek diğer alanların değerlerini değiştirmez.
/// </summary>
/// <example>
/// <code>
/// IReadOnlyList&lt;Product&gt; products = new FakeBuilder&lt;Product&gt;(new Fake(seed: 42))
///     .Rule(p => p.Name, f => f.Commerce.ProductName())
///     .Rule(p => p.Slug, (f, row) => FakeFormat.Slug(row.Get&lt;string&gt;("Name")))
///     .Rule(p => p.BasePrice, f => f.Commerce.Price(50, 5000))
///     .Rule(p => p.Description, f => f.Text.Sentence().OrNull(f, 0.2))
///     .Generate(50);
/// </code>
/// </example>
public sealed class FakeBuilder<T>
    where T : class
{
    private readonly Fake _fake;
    private readonly string _entity;
    private readonly List<(string Member, Func<Fake, FakeRow, object?> Rule)> _rules = [];
    private Func<Fake, FakeRow, T>? _instantiate;
    private Action<Fake, T, FakeRow>? _finish;

    /// <param name="fake">Tohum ve referans tarihi.</param>
    /// <param name="entity">Tohum anahtarı; varsayılan tip adı (tip adı değişirse değerler de değişir).</param>
    public FakeBuilder(Fake fake, string? entity = null)
    {
        ArgumentNullException.ThrowIfNull(fake);
        _fake = fake;
        _entity = entity ?? typeof(T).Name;
    }

    public FakeBuilder<T> Rule<TValue>(Expression<Func<T, TValue>> member, Func<Fake, TValue> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Rule(MemberName(member), (f, _) => rule(f));
    }

    public FakeBuilder<T> Rule<TValue>(Expression<Func<T, TValue>> member, Func<Fake, FakeRow, TValue> rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Rule(MemberName(member), (f, row) => rule(f, row));
    }

    /// <summary>Adla kural (ifade yazılamayan durumlar ve canfxgen için).</summary>
    public FakeBuilder<T> Rule(string member, Func<Fake, FakeRow, object?> rule)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        ArgumentNullException.ThrowIfNull(rule);
        _rules.RemoveAll(r => r.Member == member);
        _rules.Add((member, rule));
        return this;
    }

    /// <summary>Nesneyi kim kurar (varsayılan: parametresiz kurucu, protected da olabilir).</summary>
    public FakeBuilder<T> Instantiate(Func<Fake, FakeRow, T> factory)
    {
        _instantiate = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>Bütün kurallardan sonra çalışır.</summary>
    public FakeBuilder<T> FinishWith(Action<Fake, T, FakeRow> finish)
    {
        _finish = finish ?? throw new ArgumentNullException(nameof(finish));
        return this;
    }

    /// <summary>Kuralların değerleri, nesne kurmadan (alan adı → değer). HasData için anonim nesne yazarken kullanılır.</summary>
    public IReadOnlyList<FakeRow> GenerateRows(int count, int startIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var rows = new List<FakeRow>(count);
        for (int index = startIndex; index < startIndex + count; index++)
        {
            var row = new FakeRow(index);
            foreach ((string member, Func<Fake, FakeRow, object?> rule) in _rules)
                row.Set(member, rule(_fake.For(_entity, index, member), row));
            rows.Add(row);
        }

        return rows;
    }

    public IReadOnlyList<T> Generate(int count, int startIndex = 0)
    {
        var items = new List<T>(count);
        foreach (FakeRow row in GenerateRows(count, startIndex))
        {
            T item = _instantiate is null
                ? (T)(Activator.CreateInstance(typeof(T), nonPublic: true)
                    ?? throw new InvalidOperationException($"{typeof(T).Name} kurulamadı: Instantiate(...) ver."))
                : _instantiate(_fake.For(_entity, row.Index, "<new>"), row);

            foreach ((string member, object? value) in row.Values)
                SetMember(item, member, value);

            _finish?.Invoke(_fake.For(_entity, row.Index, "<finish>"), item, row);
            items.Add(item);
        }

        return items;
    }

    public T GenerateOne(int index = 0) => Generate(1, index)[0];

    private static string MemberName<TValue>(Expression<Func<T, TValue>> member)
    {
        ArgumentNullException.ThrowIfNull(member);
        Expression body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : member.Body;
        return body is MemberExpression { Member: PropertyInfo or FieldInfo } m
            ? m.Member.Name
            : throw new ArgumentException($"Alan ifadesi bekleniyordu (p => p.Name): {member}", nameof(member));
    }

    /// <summary>Property'ye (protected / private setter dahil), yoksa auto-property'nin arka alanına yazar.</summary>
    private static void SetMember(T item, string member, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (Type? type = item.GetType(); type is not null; type = type.BaseType)
        {
            PropertyInfo? property = type.GetProperty(member, flags | BindingFlags.DeclaredOnly);
            if (property?.GetSetMethod(nonPublic: true) is { } setter)
            {
                setter.Invoke(item, [value]);
                return;
            }

            FieldInfo? field = type.GetField(member, flags | BindingFlags.DeclaredOnly)
                ?? type.GetField($"<{member}>k__BackingField", flags | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                field.SetValue(item, value);
                return;
            }
        }

        throw new InvalidOperationException($"{typeof(T).Name}.{member} yazılamıyor (setter ya da alan yok).");
    }
}
