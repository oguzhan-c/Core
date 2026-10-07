using System.Text;

namespace Can.Core.Reporting.Export;

/// <summary>
/// PDF'in standart yazı tipleri (Helvetica, Helvetica-Bold): her PDF okuyucuda vardır, gömülmez (dosya küçük kalır).
/// Kodlama WinAnsi + Türkçe harfler (Windows-1254 ile aynı yerler: Ğ ğ İ ı Ş ş); bu glifler standart Latin kümesinde
/// bulunur. Genişlikler Adobe'nin Helvetica AFM ölçülerinden (1000 birim = yazı boyutu).
/// </summary>
internal static class PdfFont
{
    /// <summary>Türkçe harflerin WinAnsi'de yerini aldığı kodlar ve glif adları (yazı tipi /Differences).</summary>
    public const string Differences = "[208 /Gbreve 221 /Idotaccent 222 /Scedilla 240 /gbreve 253 /dotlessi 254 /scedilla]";

    // ASCII 32-126
    private static readonly short[] Regular =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private static readonly short[] Bold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    private static readonly Dictionary<char, byte> WinAnsiSpecials = new()
    {
        ['€'] = 128, ['‚'] = 130, ['„'] = 132, ['…'] = 133, ['†'] = 134, ['‡'] = 135, ['‰'] = 137, ['Š'] = 138,
        ['‹'] = 139, ['Œ'] = 140, ['Ž'] = 142, ['‘'] = 145, ['’'] = 146, ['“'] = 147, ['”'] = 148, ['•'] = 149,
        ['–'] = 150, ['—'] = 151, ['™'] = 153, ['š'] = 154, ['›'] = 155, ['œ'] = 156, ['ž'] = 158, ['Ÿ'] = 159,
        ['Ğ'] = 208, ['İ'] = 221, ['Ş'] = 222, ['ğ'] = 240, ['ı'] = 253, ['ş'] = 254,
    };

    /// <summary>Yerleri Türkçe harflere verilen Latin-1 karakterleri (Ð Ý Þ ð ý þ) yazılamaz.</summary>
    private static readonly HashSet<char> Displaced = ['Ð', 'Ý', 'Þ', 'ð', 'ý', 'þ'];

    /// <summary>Yazı tipinde olmayan karakterleri yazılabilir karşılıklarına çevirir.</summary>
    public static string Prepare(string text) =>
        text.Replace("₺", "TL", StringComparison.Ordinal).Replace(' ', ' ').Replace(' ', ' ');

    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            bytes[i] = EncodeChar(text[i]);
        return bytes;
    }

    private static byte EncodeChar(char c)
    {
        if (c < 32)
            return (byte)' ';
        if (c < 127)
            return (byte)c;
        if (WinAnsiSpecials.TryGetValue(c, out byte special))
            return special;
        if (c is >= ' ' and <= 'ÿ' && !Displaced.Contains(c))
            return (byte)c;
        return (byte)'?';
    }

    /// <summary>Metnin genişliği (punto).</summary>
    public static double Width(string text, bool bold, double size)
    {
        double units = 0;
        foreach (char c in text)
            units += CharWidth(c, bold);
        return units * size / 1000;
    }

    private static int CharWidth(char c, bool bold)
    {
        short[] table = bold ? Bold : Regular;
        if (c is >= ' ' and <= '~')
            return table[c - 32];

        switch (c)
        {
            case 'ı':
                return 278;
            case 'ß' or 'ø':
                return 611;
            case 'Æ' or '…' or '—' or '‰' or 'Œ' or '™':
                return 1000;
            case 'æ' or 'œ':
                return 889;
            case 'Ø':
                return 778;
            case '€' or '–':
                return 556;
            case '‘' or '’' or '‚':
                return bold ? 278 : 222;
            case '“' or '”' or '„':
                return bold ? 500 : 333;
            case '•':
                return 350;
            case '°':
                return 400;
            case '×' or '÷' or '±':
                return 584;
            case ' ':
                return 278;
        }

        // aksanlı harfler: temel harfin genişliği (Ç → C, ü → u, Ş → S ...)
        string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        char baseChar = decomposed.Length > 0 ? decomposed[0] : c;
        return baseChar is >= ' ' and <= '~' ? table[baseChar - 32] : 556;
    }
}
