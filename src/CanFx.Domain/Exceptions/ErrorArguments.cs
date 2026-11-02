namespace CanFx.Domain.Exceptions;

/// <summary>
/// Hata mesajındaki <c>{ad}</c> yer tutucularının değerleri. Değerler exception'ın <see cref="Exception.Data"/>'sında
/// durur; hata yanıtı yazılırken hem çeviri metnine (<c>Localization/*.json</c>) hem varsayılan mesaja yerleştirilir.
/// Her exception tipiyle çalışır.
/// </summary>
/// <example>
/// <code>
/// // mesaj: "{name} adında bir marka zaten var."   çeviri (en.json): "brand.name_exists": "A brand named {name} already exists."
/// throw new BusinessException(BrandsBusinessMessages.NameExists) { Code = BrandsBusinessMessages.NameExistsCode }.With("name", name);
/// </code>
/// </example>
public static class ErrorArguments
{
    /// <summary>Yer tutucu değeri ekler; zincirlenebilir.</summary>
    public static TException With<TException>(this TException exception, string name, object? value)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        exception.Data[name] = value;
        return exception;
    }

    /// <summary>Exception'a eklenmiş yer tutucu değerleri (adı metin olanlar).</summary>
    public static IReadOnlyDictionary<string, object?> GetArguments(this Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in exception.Data)
        {
            if (entry.Key is string key)
                values[key] = entry.Value;
        }

        return values;
    }
}
