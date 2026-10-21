using System.Globalization;
using System.Text;

namespace CanFx.Generator;

/// <summary>Adlandırma yardımcıları: çoğul, camelCase, kebab-case, snake_case.</summary>
public static class Names
{
    /// <summary>İngilizce çoğul (basit kurallar): Category → Categories, Box → Boxes, Brand → Brands.</summary>
    public static string Plural(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name.EndsWith('y') && name.Length > 1 && !IsVowel(name[^2]))
            return name[..^1] + "ies";

        if (name.EndsWith('s') || name.EndsWith('x') || name.EndsWith('z') || name.EndsWith("ch", StringComparison.Ordinal) || name.EndsWith("sh", StringComparison.Ordinal))
            return name + "es";

        return name + "s";
    }

    /// <summary>İngilizce tekil (basit kurallar): Categories → Category, Boxes → Box, Brands → Brand.</summary>
    public static string Singular(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name.EndsWith("ies", StringComparison.Ordinal) && name.Length > 3)
            return name[..^3] + "y";

        foreach (string suffix in new[] { "sses", "xes", "zes", "ches", "shes" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
                return name[..^2];
        }

        return name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) ? name[..^1] : name;
    }

    public static string Pascal(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name[1..];

    public static string Camel(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>ProductCategories → product-categories (adres ve yetki adları).</summary>
    public static string Kebab(string name) => Separate(name, '-');

    /// <summary>ProductCategory → product_category (hata kodları).</summary>
    public static string Snake(string name) => Separate(name, '_');

    /// <summary>ProductCategory → "Product category" (açıklama metinleri).</summary>
    public static string Words(string name)
    {
        string words = Separate(name, ' ');
        return words.Length == 0 ? words : char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..];
    }

    private static string Separate(string name, char separator)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool boundary = char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (boundary)
                builder.Append(separator);
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static bool IsVowel(char c) => "aeiouAEIOU".Contains(c, StringComparison.Ordinal);
}
