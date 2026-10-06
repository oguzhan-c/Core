using System.Globalization;
using System.Text;
using Can.Core.Domain.Results;
using Microsoft.Extensions.Localization;

namespace Can.Core.Localization;

/// <summary>
/// <see cref="IStringLocalizer"/>: metni o anki UI kültürüne (<see cref="CultureInfo.CurrentUICulture"/>) göre
/// <see cref="JsonLocalizationStore"/>'dan okur. Tüm anahtarlar tek bir sözlükte; <c>IStringLocalizer&lt;T&gt;</c>
/// de aynı sözlüğü kullanır. Bulunamazsa anahtarın kendisi döner (<c>ResourceNotFound = true</c>).
/// </summary>
public sealed class JsonStringLocalizer : IStringLocalizer
{
    private readonly JsonLocalizationStore _store;

    public JsonStringLocalizer(JsonLocalizationStore store) => _store = store;

    public LocalizedString this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            string? value = _store.Find(name, CultureInfo.CurrentUICulture);
            return new LocalizedString(name, value ?? name, resourceNotFound: value is null);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            LocalizedString template = this[name];
            return new LocalizedString(
                name,
                string.Format(CultureInfo.CurrentCulture, template.Value, arguments),
                template.ResourceNotFound
            );
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        _store.GetAll(CultureInfo.CurrentUICulture, includeParentCultures).Select(p => new LocalizedString(p.Key, p.Value, false));
}

/// <summary>Her tip için aynı (paylaşılan) sözlüğü veren fabrika.</summary>
public sealed class JsonStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly JsonStringLocalizer _localizer;

    public JsonStringLocalizerFactory(JsonLocalizationStore store) => _localizer = new JsonStringLocalizer(store);

    public IStringLocalizer Create(Type resourceSource) => _localizer;

    public IStringLocalizer Create(string baseName, string location) => _localizer;
}

public static class LocalizerExtensions
{
    /// <summary>
    /// Hatanın kodu çeviri sözlüğünde varsa o metni (içindeki <c>{ad}</c> yer tutucuları <see cref="Error.Metadata"/>'dan
    /// doldurulur), yoksa <see cref="Error.Description"/>'ı döner.
    /// </summary>
    /// <example>
    /// <c>"product.out_of_stock": "{name} ürününden yalnızca {available} adet kaldı."</c> +
    /// <c>Error.Failure("product.out_of_stock", "...").WithMetadata("name", "Chai").WithMetadata("available", 3)</c>
    /// </example>
    public static string Describe(this IStringLocalizer localizer, Error error)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(error);

        LocalizedString text = localizer[error.Code];
        return text.ResourceNotFound ? error.Description : FormatNamed(text.Value, error.Metadata);
    }

    /// <summary>Metin bulunamazsa <paramref name="fallback"/>.</summary>
    public static string GetOrDefault(this IStringLocalizer localizer, string key, string fallback)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        LocalizedString text = localizer[key];
        return text.ResourceNotFound ? fallback : text.Value;
    }

    /// <summary><c>{ad}</c> yer tutucularını değerlerle doldurur; bilinmeyen yer tutucular olduğu gibi kalır.</summary>
    public static string FormatNamed(string template, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0 || !template.Contains('{', StringComparison.Ordinal))
            return template;

        var builder = new StringBuilder(template.Length);
        int index = 0;
        while (index < template.Length)
        {
            int open = template.IndexOf('{', index);
            int close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);
            string name = template[(open + 1)..close];
            if (values.TryGetValue(name, out object? value))
                builder.Append(Convert.ToString(value, CultureInfo.CurrentCulture));
            else
                builder.Append(template, open, close - open + 1);

            index = close + 1;
        }

        return builder.ToString();
    }
}
