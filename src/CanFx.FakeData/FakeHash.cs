using System.Globalization;
using System.Text;

namespace CanFx.FakeData;

/// <summary>
/// Kararlı 64 bit özet: aynı girdi her makinede, her .NET sürümünde aynı sonucu verir (<see cref="string.GetHashCode()"/>
/// her süreçte farklıdır, kullanılamaz). FNV-1a (UTF-8) + SplitMix64 karıştırıcısı.
/// </summary>
public static class FakeHash
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>Parçaların sıralı özeti. Parçalar ayırıcıyla birleştirilir: ("ab", "c") ile ("a", "bc") farklıdır.</summary>
    public static ulong Of(ulong seed, params object?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ulong hash = Mix(seed ^ FnvOffset);
        foreach (object? part in parts)
        {
            hash = Append(hash, Text(part));
            hash = Mix(hash ^ 0x1FUL);   // parça ayırıcı
        }

        return Mix(hash);
    }

    /// <summary>SplitMix64 karıştırıcısı: bitleri iyi dağıtır, ardışık girdiler ilişkisiz çıktılar verir.</summary>
    public static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static ulong Append(ulong hash, string text)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }

    // Kültürden bağımsız metin: 1.5 her makinede "1.5", tarih ISO.
    private static string Text(object? part) => part switch
    {
        null => "\0null",
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => part.ToString() ?? string.Empty,
    };
}
