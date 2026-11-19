using System.Text;

namespace CanFx.FakeData;

/// <summary>
/// Tohumlu sayı üretici (SplitMix64). <see cref="Random"/> yerine kendi algoritması: .NET sürümü değişse de aynı tohum
/// aynı diziyi verir.
/// </summary>
public sealed class FakeRandom
{
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Alphanumerics = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private ulong _state;

    public FakeRandom(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        ulong z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>[0, 1) aralığında sayı.</summary>
    public double Double() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary><paramref name="min"/> ile <paramref name="max"/> arasında (ikisi de dahil) tam sayı.</summary>
    public int Int(int min, int max)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max);
        return (int)Long(min, max);
    }

    /// <summary><paramref name="min"/> ile <paramref name="max"/> arasında (ikisi de dahil) tam sayı.</summary>
    public long Long(long min, long max)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max);
        ulong range = (ulong)(max - min) + 1;
        return range == 0 ? (long)NextUInt64() : min + (long)(NextUInt64() % range);
    }

    /// <summary>Aralıkta, <paramref name="decimals"/> ondalıklı tutar.</summary>
    public decimal Decimal(decimal min, decimal max, int decimals = 2)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max);
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        decimal value = min + (decimal)Double() * (max - min);
        return Math.Clamp(Math.Round(value, decimals, MidpointRounding.AwayFromZero), min, max);
    }

    /// <summary><paramref name="probability"/> olasılıkla true (0,7 → yaklaşık %70).</summary>
    public bool Bool(double probability = 0.5) => Double() < probability;

    public T Pick<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            throw new ArgumentException("Seçilecek öğe yok.", nameof(items));
        return items[(int)(NextUInt64() % (ulong)items.Count)];
    }

    /// <summary>Ağırlığa göre seçim: (("Paid", 7), ("Cancelled", 1)) → Paid yaklaşık 7 kat daha sık.</summary>
    public T Weighted<T>(params (T Item, double Weight)[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        double total = items.Sum(i => Math.Max(0, i.Weight));
        if (items.Length == 0 || total <= 0)
            throw new ArgumentException("Ağırlığı sıfırdan büyük en az bir öğe gerekli.", nameof(items));

        double roll = Double() * total;
        foreach ((T item, double weight) in items)
        {
            roll -= Math.Max(0, weight);
            if (roll < 0)
                return item;
        }

        return items[^1].Item;
    }

    /// <summary>Listeden <paramref name="count"/> farklı öğe (sırası karışık).</summary>
    public IReadOnlyList<T> PickMany<T>(IReadOnlyList<T> items, int count)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var copy = items.ToList();
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = (int)(NextUInt64() % (ulong)(i + 1));
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy.Take(Math.Min(count, copy.Count)).ToList();
    }

    /// <summary><paramref name="length"/> haneli rakam dizisi (ilki sıfır olabilir).</summary>
    public string Digits(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var builder = new StringBuilder(length);
        for (int i = 0; i < length; i++)
            builder.Append((char)('0' + (int)(NextUInt64() % 10)));
        return builder.ToString();
    }

    /// <summary>Desen: <c>#</c> rakam, <c>?</c> büyük harf, <c>*</c> harf ya da rakam; diğerleri olduğu gibi ("SKU-#####").</summary>
    public string Pattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var builder = new StringBuilder(pattern.Length);
        foreach (char c in pattern)
        {
            builder.Append(c switch
            {
                '#' => (char)('0' + (int)(NextUInt64() % 10)),
                '?' => Letters[(int)(NextUInt64() % (ulong)Letters.Length)],
                '*' => Alphanumerics[(int)(NextUInt64() % (ulong)Alphanumerics.Length)],
                _ => c,
            });
        }

        return builder.ToString();
    }

    /// <summary><paramref name="count"/> rastgele bayt.</summary>
    public byte[] Bytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i += 8)
        {
            ulong value = NextUInt64();
            for (int j = 0; j < 8 && i + j < count; j++)
                bytes[i + j] = (byte)(value >> (8 * j));
        }

        return bytes;
    }
}
