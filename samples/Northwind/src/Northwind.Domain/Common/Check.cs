using Can.Core.Domain.Exceptions;

namespace Northwind.Domain.Common;

/// <summary>Aggregate'lerin kendi kurallarını doğrulamak için küçük yardımcılar; ihlalde <see cref="BusinessException"/> (HTTP 400).</summary>
internal static class Check
{
    public static string Required(string? value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new BusinessException($"{field} boş olamaz.");

        return Length(value.Trim(), field, maxLength)!;
    }

    public static string? Optional(string? value, string field, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Length(value.Trim(), field, maxLength);

    public static decimal NotNegative(decimal value, string field) =>
        value < 0 ? throw new BusinessException($"{field} negatif olamaz.") : value;

    public static int NotNegative(int value, string field) =>
        value < 0 ? throw new BusinessException($"{field} negatif olamaz.") : value;

    public static int Positive(int value, string field) =>
        value <= 0 ? throw new BusinessException($"{field} sıfırdan büyük olmalı.") : value;

    private static string? Length(string value, string field, int maxLength) =>
        value.Length > maxLength ? throw new BusinessException($"{field} en fazla {maxLength} karakter olabilir.") : value;
}
