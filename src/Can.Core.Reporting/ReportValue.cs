using System.Globalization;
using System.Text.Json;

namespace Can.Core.Reporting;

/// <summary>Değer dönüşümleri ve karşılaştırma: sayılar decimal, tarihler DateTime olarak ele alınır.</summary>
internal static class ReportValue
{
    /// <summary>Gruplama anahtarı ve karşılaştırma için tek biçime getirir.</summary>
    public static object? Normalize(object? value) =>
        value switch
        {
            null or DBNull => null,
            JsonElement json => FromJson(json),
            string or decimal or bool or DateTime => value,
            DateTimeOffset dto => dto.DateTime,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            Enum e => e.ToString(),
            Guid or char or TimeSpan => value.ToString(),
            _ => ToDecimal(value) is { } number ? number : value,
        };

    public static decimal? ToDecimal(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case decimal d:
                return d;
            case int or long or short or byte or sbyte or ushort or uint or ulong:
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            case double or float:
                double x = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(x) || double.IsInfinity(x) || Math.Abs(x) > 7.9e28)
                    return null;
                return (decimal)x;
            case JsonElement { ValueKind: JsonValueKind.Number } json:
                return json.GetDecimal();
            default:
                return null;
        }
    }

    public static DateTime? ToDate(object? value) =>
        value switch
        {
            DateTime d => d,
            DateTimeOffset dto => dto.DateTime,
            DateOnly date => date.ToDateTime(TimeOnly.MinValue),
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } json when json.TryGetDateTime(out DateTime parsed) => parsed,
            _ => null,
        };

    public static bool IsTrue(object? value) => value is true;

    public static string? ToText(object? value, CultureInfo culture) =>
        value switch
        {
            null => null,
            string s => s,
            DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("d", culture) : d.ToString("g", culture),
            IFormattable f => f.ToString(null, culture),
            _ => value.ToString(),
        };

    /// <summary>
    /// Sıralama karşılaştırması: boşlar başta; sayılar sayı, tarihler tarih (metin tarihe çevrilebiliyorsa) olarak;
    /// karşılaştırılamayanlar metin olarak.
    /// </summary>
    public static int Compare(object? a, object? b, CompareInfo? compare = null)
    {
        a = Normalize(a);
        b = Normalize(b);
        if (a is null || b is null)
            return a is null ? (b is null ? 0 : -1) : 1;

        if (a is decimal da && b is decimal db)
            return da.CompareTo(db);
        if (a is bool ba && b is bool bb)
            return ba.CompareTo(bb);
        if ((a is DateTime || b is DateTime) && ToDate(a) is { } ta && ToDate(b) is { } tb)
            return ta.CompareTo(tb);

        string sa = a as string ?? Convert.ToString(a, CultureInfo.InvariantCulture) ?? string.Empty;
        string sb = b as string ?? Convert.ToString(b, CultureInfo.InvariantCulture) ?? string.Empty;
        return (compare ?? CultureInfo.InvariantCulture.CompareInfo).Compare(sa, sb, CompareOptions.None);
    }

    public static bool AreEqual(object? a, object? b) => Compare(a, b, CultureInfo.InvariantCulture.CompareInfo) == 0 && (a is null) == (b is null);

    private static object? FromJson(JsonElement json) =>
        json.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => json.GetDecimal(),
            JsonValueKind.String => json.GetString(),
            _ => json.GetRawText(),
        };
}
