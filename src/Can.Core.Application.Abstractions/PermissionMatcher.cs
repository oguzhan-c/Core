namespace Can.Core.Application;

/// <summary>
/// Yetki eşleştirme: büyük/küçük harf duyarsız; <c>"*"</c> her şeyi, <c>"products.*"</c> <c>products.</c> ile
/// başlayan her yetkiyi (ve <c>products</c>'ın kendisini) kapsar.
/// </summary>
public static class PermissionMatcher
{
    public static bool IsGranted(IEnumerable<string> granted, string required)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentException.ThrowIfNullOrWhiteSpace(required);

        foreach (string permission in granted)
        {
            if (Covers(permission, required))
                return true;
        }

        return false;
    }

    public static bool Covers(string granted, string required)
    {
        if (string.IsNullOrWhiteSpace(granted))
            return false;

        if (granted == "*" || string.Equals(granted, required, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!granted.EndsWith(".*", StringComparison.Ordinal))
            return false;

        ReadOnlySpan<char> prefix = granted.AsSpan(0, granted.Length - 2);
        return required.AsSpan().Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || (required.Length > prefix.Length + 1
                && required[prefix.Length] == '.'
                && required.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
