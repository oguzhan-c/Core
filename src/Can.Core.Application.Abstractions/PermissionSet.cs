namespace Can.Core.Application;

/// <summary>
/// Yetki KÜMELERİ üzerinde işlemler: bir yetki deseninin (joker dahil) başka desenlerce tamamen kapsanıp kapsanmadığı,
/// alt küme kontrolü ve ad doğrulama. Yetki verme kurallarının (kimse sahip olmadığı yetkiyi veremez) temelidir.
/// </summary>
/// <remarks>
/// <see cref="PermissionMatcher"/> tek bir somut yetkiyi kontrol eder; burada istenen değer de joker olabilir:
/// <c>"colors.*"</c> yalnızca <c>"*"</c>, <c>"colors.*"</c> ya da daha geniş bir joker (<c>"app.*"</c> →
/// <c>"app.colors.*"</c>) tarafından kapsanır; <c>"colors.read"</c> + <c>"colors.write"</c> <c>"colors.*"</c>'ı
/// KAPSAMAZ (ileride eklenecek <c>colors.delete</c>'i de vermek olurdu).
/// </remarks>
public static class PermissionSet
{
    /// <summary>Tüm yetkiler.</summary>
    public const string All = "*";

    /// <summary><paramref name="granted"/> deseni <paramref name="required"/> deseninin TAMAMINI kapsıyor mu.</summary>
    public static bool CoversPattern(string granted, string required)
    {
        if (string.IsNullOrWhiteSpace(granted) || string.IsNullOrWhiteSpace(required))
            return false;

        if (granted == All)
            return true;

        if (required == All)
            return false;

        if (!required.EndsWith(".*", StringComparison.Ordinal))
            return PermissionMatcher.Covers(granted, required);

        if (!granted.EndsWith(".*", StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> requiredPrefix = required.AsSpan(0, required.Length - 2);
        ReadOnlySpan<char> grantedPrefix = granted.AsSpan(0, granted.Length - 2);
        return requiredPrefix.Equals(grantedPrefix, StringComparison.OrdinalIgnoreCase)
            || (requiredPrefix.Length > grantedPrefix.Length
                && requiredPrefix[grantedPrefix.Length] == '.'
                && requiredPrefix[..grantedPrefix.Length].Equals(grantedPrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><paramref name="granted"/> içindeki desenlerden biri <paramref name="required"/>'ı tamamen kapsıyor mu.</summary>
    public static bool IsGranted(IEnumerable<string> granted, string required)
    {
        ArgumentNullException.ThrowIfNull(granted);
        foreach (string permission in granted)
        {
            if (CoversPattern(permission, required))
                return true;
        }

        return false;
    }

    /// <summary><paramref name="granted"/>'ın kapsamadığı istenen yetkiler (boşsa <paramref name="requested"/> bir alt kümedir).</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<string> requested, IEnumerable<string> granted)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(granted);
        IReadOnlyCollection<string> grantedList = granted as IReadOnlyCollection<string> ?? granted.ToArray();
        return requested
            .Where(r => !IsGranted(grantedList, r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary><paramref name="requested"/>'ın tamamı <paramref name="granted"/> tarafından kapsanıyor mu.</summary>
    public static bool IsSubset(IEnumerable<string> requested, IEnumerable<string> granted) => Missing(requested, granted).Count == 0;

    /// <summary>
    /// Geçerli yetki adı: küçük harf, rakam ve <c>-</c>/<c>_</c> içeren, noktayla ayrılmış bölümler
    /// (<c>colors.read</c>, <c>colors.sync-color</c>); joker yalnızca tek başına (<c>*</c>) ya da son bölüm (<c>colors.*</c>).
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            return false;

        if (name == All)
            return true;

        string[] segments = name.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.Length == 0)
                return false;

            if (segment == "*")
            {
                if (i != segments.Length - 1 || i == 0)
                    return false;
                continue;
            }

            foreach (char c in segment)
            {
                if (!(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                    return false;
            }
        }

        return true;
    }
}
