using System.Text;

namespace Can.Core.Sms;

public enum SmsEncoding
{
    /// <summary>GSM 03.38 7-bit alfabe: tek SMS 160, parçalı 153 karakter.</summary>
    Gsm7,

    /// <summary>Unicode (UCS-2): tek SMS 70, parçalı 67 karakter. ğ, ı, ş, emoji ... içeren metinler.</summary>
    Ucs2,
}

/// <param name="Encoding">Kodlama.</param>
/// <param name="Length">Kodlamaya göre karakter sayısı (GSM'de <c>€ [ ] { }</c> gibi karakterler 2 sayılır).</param>
/// <param name="Segments">Kaç SMS olarak gideceği (faturalanacağı).</param>
/// <param name="Remaining">Son parçada kalan karakter.</param>
public sealed record SmsTextInfo(SmsEncoding Encoding, int Length, int Segments, int Remaining);

/// <summary>SMS metni hesapları: kodlama, parça sayısı ve GSM'e dönüştürme.</summary>
public static class SmsText
{
    // GSM 03.38 temel tablo (ESC hariç) ve 2 karakter sayılan genişletme tablosu.
    private const string Basic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    private const string Extension = "^{}\\[~]|€\f";

    private static readonly HashSet<char> BasicSet = [.. Basic];
    private static readonly HashSet<char> ExtensionSet = [.. Extension];

    /// <summary>Türkçe ve sık görülen karakterlerin GSM karşılıkları (ö, ü, Ç, Ö, Ü zaten GSM'de var).</summary>
    private static readonly Dictionary<char, string> Transliterations = new()
    {
        ['ç'] = "c",
        ['ğ'] = "g",
        ['Ğ'] = "G",
        ['ı'] = "i",
        ['İ'] = "I",
        ['ş'] = "s",
        ['Ş'] = "S",
        ['â'] = "a",
        ['Â'] = "A",
        ['î'] = "i",
        ['Î'] = "I",
        ['û'] = "u",
        ['Û'] = "U",
        ['‘'] = "'",
        ['’'] = "'",
        ['“'] = "\"",
        ['”'] = "\"",
        ['–'] = "-",
        ['—'] = "-",
        ['…'] = "...",
        [' '] = " ",
        ['\t'] = " ",
    };

    public static bool IsGsm7(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (!BasicSet.Contains(c) && !ExtensionSet.Contains(c))
                return false;
        }

        return true;
    }

    /// <summary>Metnin kaç SMS edeceğini hesaplar.</summary>
    /// <example><c>SmsText.Analyze("Doğrulama kodun: 123456")</c> → UCS-2 ("ğ"), 23 karakter, 1 SMS.</example>
    public static SmsTextInfo Analyze(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (IsGsm7(text))
        {
            int length = text.Sum(c => ExtensionSet.Contains(c) ? 2 : 1);
            return Info(SmsEncoding.Gsm7, length, single: 160, multi: 153);
        }

        // UCS-2: UTF-16 kod birimi sayısı (emoji gibi vekil çiftler 2 sayılır).
        return Info(SmsEncoding.Ucs2, text.Length, single: 70, multi: 67);
    }

    /// <summary>
    /// GSM'de olmayan Türkçe karakterleri en yakın karşılığına çevirir (<c>ğ → g</c>, <c>ş → s</c>, <c>ı → i</c>):
    /// mesaj UCS-2 yerine GSM-7 gider, tek SMS'e 70 yerine 160 karakter sığar. Karşılığı olmayan karakter (emoji)
    /// olduğu gibi kalır; o zaman metin yine UCS-2 olur.
    /// </summary>
    public static string ToGsm(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (Transliterations.TryGetValue(c, out string? replacement))
                builder.Append(replacement);
            else
                builder.Append(c);
        }

        return builder.ToString();
    }

    private static SmsTextInfo Info(SmsEncoding encoding, int length, int single, int multi)
    {
        if (length == 0)
            return new SmsTextInfo(encoding, 0, 0, single);
        if (length <= single)
            return new SmsTextInfo(encoding, length, 1, single - length);

        int segments = (length + multi - 1) / multi;
        return new SmsTextInfo(encoding, length, segments, (segments * multi) - length);
    }
}
