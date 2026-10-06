using Can.Core.Domain.Results;

namespace Northwind.Domain.Common;

/// <summary>
/// Aggregate'lerin alan kontrolleri. Her kontrol geçerse <see langword="null"/>, geçmezse doğrulama hatası döner;
/// <see cref="Result.Validate"/> ile birlikte kullanılır ve tüm hatalar tek seferde toplanır:
/// <code>
/// Result&lt;Success&gt; valid = Result.Validate(Check.Required(name, "Ad", 50), Check.NotNegative(price, "Fiyat"));
/// if (valid.IsFailure)
///     return valid.Errors;
/// </code>
/// </summary>
internal static class Check
{
    public static Error? Required(string? value, string field, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? Error.Validation("required", $"{field} boş olamaz.", field) : Length(value, field, maxLength);

    public static Error? Optional(string? value, string field, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Length(value, field, maxLength);

    public static Error? NotNegative(decimal value, string field) =>
        value < 0 ? Error.Validation("not_negative", $"{field} negatif olamaz.", field) : null;

    public static Error? NotNegative(int value, string field) =>
        value < 0 ? Error.Validation("not_negative", $"{field} negatif olamaz.", field) : null;

    public static Error? Positive(int value, string field) =>
        value <= 0 ? Error.Validation("positive", $"{field} sıfırdan büyük olmalı.", field) : null;

    /// <summary>Kontrolden geçmiş zorunlu metin: baştaki/sondaki boşluklar atılır.</summary>
    public static string Clean(string value) => value.Trim();

    /// <summary>Kontrolden geçmiş isteğe bağlı metin: boşsa <see langword="null"/>.</summary>
    public static string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Error? Length(string value, string field, int maxLength) =>
        value.Trim().Length > maxLength
            ? Error.Validation("max_length", $"{field} en fazla {maxLength} karakter olabilir.", field)
            : null;
}
