namespace Can.Core.Sms;

/// <summary>Telefon numarasını uluslararası biçime (E.164: <c>+</c> ve 8-15 rakam) çevirir.</summary>
public static class PhoneNumbers
{
    /// <summary>
    /// Boşluk, tire, nokta ve parantezleri atar; ülke kodu yoksa <paramref name="defaultCountryCode"/> ekler.
    /// <list type="bullet">
    /// <item><c>+90 532 123 45 67</c>, <c>0090 532 ...</c> → <c>+905321234567</c></item>
    /// <item><c>0532 123 45 67</c> (ulusal, baştaki 0 atılır) → <c>+905321234567</c></item>
    /// <item><c>532 123 45 67</c> → <c>+905321234567</c></item>
    /// </list>
    /// </summary>
    /// <param name="input">Kullanıcının yazdığı numara.</param>
    /// <param name="defaultCountryCode">Ülke kodu yazılmamışsa kullanılacak kod (<c>90</c>).</param>
    /// <param name="e164">Sonuç.</param>
    public static bool TryNormalize(string? input, string defaultCountryCode, out string e164)
    {
        e164 = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string trimmed = input.Trim();
        bool international = trimmed.StartsWith('+');
        Span<char> digits = stackalloc char[trimmed.Length];
        int length = 0;

        foreach (char c in trimmed.AsSpan(international ? 1 : 0))
        {
            if (char.IsAsciiDigit(c))
                digits[length++] = c;
            else if (c is not (' ' or '-' or '.' or '(' or ')' or '/'))
                return false; // harf vb.
        }

        string number = new(digits[..length]);
        string country = defaultCountryCode.TrimStart('+');

        if (!international)
        {
            if (number.StartsWith("00", StringComparison.Ordinal))
                number = number[2..]; // 00 ile uluslararası
            else if (number.StartsWith('0'))
                number = country + number[1..]; // ulusal: 0 + alan kodu
            else if (!number.StartsWith(country, StringComparison.Ordinal) || number.Length <= 10)
                number = country + number; // yalnızca abone numarası
        }

        if (number.Length is < 8 or > 15 || number[0] == '0')
            return false;

        e164 = "+" + number;
        return true;
    }

    /// <summary>Loglarda göstermek için: <c>+905321234567</c> → <c>+90532*****67</c>.</summary>
    public static string Mask(string number)
    {
        if (string.IsNullOrEmpty(number) || number.Length <= 7)
            return "***";
        return string.Concat(number.AsSpan(0, 6), new string('*', number.Length - 8), number.AsSpan(number.Length - 2));
    }
}
