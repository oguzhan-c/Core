using System.Globalization;
using System.Text;

namespace CanFx.FakeData;

/// <summary>Metin yardımcıları: Türkçe karakterleri ASCII'ye çevirme, slug, kırpma.</summary>
public static class FakeFormat
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>"Gömlek Şık" → "Gomlek Sik".</summary>
    public static string Ascii(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(c switch
            {
                'ç' => 'c', 'Ç' => 'C', 'ğ' => 'g', 'Ğ' => 'G', 'ı' => 'i', 'İ' => 'I',
                'ö' => 'o', 'Ö' => 'O', 'ş' => 's', 'Ş' => 'S', 'ü' => 'u', 'Ü' => 'U',
                'â' => 'a', 'Â' => 'A', 'î' => 'i', 'Î' => 'I', 'û' => 'u', 'Û' => 'U',
                _ => c,
            });
        }

        return builder.ToString();
    }

    /// <summary>"Pamuklu Slim Gömlek" → "pamuklu-slim-gomlek".</summary>
    public static string Slug(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string ascii = Ascii(value).ToLowerInvariant();
        var builder = new StringBuilder(ascii.Length);
        bool dash = false;
        foreach (char c in ascii)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
                dash = false;
            }
            else if (!dash && builder.Length > 0)
            {
                builder.Append('-');
                dash = true;
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    /// <summary>İlk harfi büyük (Türkçe kurallarıyla: "ışık" → "Işık", "iyi" → "İyi").</summary>
    public static string Capitalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 0 ? value : value[..1].ToUpper(Turkish) + value[1..];
    }

    /// <summary>En çok <paramref name="maxLength"/> karakter (sondaki boşluk silinir).</summary>
    public static string Truncate(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        return value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
    }
}

/// <summary>Üretilen değere uygulanan son işlemler.</summary>
public static class FakeValueExtensions
{
    /// <summary><paramref name="nullProbability"/> olasılıkla null (0,2 → yaklaşık %20).</summary>
    public static T? OrNull<T>(this T value, Fake fake, double nullProbability)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(fake);
        return fake.Random.Bool(nullProbability) ? null : value;
    }

    /// <summary><paramref name="nullProbability"/> olasılıkla null (değer tipleri için).</summary>
    public static T? OrNullValue<T>(this T value, Fake fake, double nullProbability)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(fake);
        return fake.Random.Bool(nullProbability) ? null : value;
    }
}
