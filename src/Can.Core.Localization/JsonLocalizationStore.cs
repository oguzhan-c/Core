using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Can.Core.Localization;

/// <summary>
/// Tüm çevirileri açılışta bir kez okuyup bellekte tutar. Kaynak sırası (sonraki öncekini ezer): gömülü kaynaklar
/// (eklenme sırasıyla), sonra uygulamanın çeviri klasörü.
/// </summary>
public sealed class JsonLocalizationStore
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _cultures;
    private readonly string _defaultCulture;

    public JsonLocalizationStore(CanLocalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _defaultCulture = options.DefaultCulture;

        var cultures = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (Assembly assembly in options.ResourceAssemblies.Distinct())
        {
            foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                if (CultureOf(resource) is not { } culture)
                    continue;

                using Stream stream = assembly.GetManifestResourceStream(resource)!;
                Load(cultures, culture, stream, resource);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ResourcesPath))
        {
            string directory = Path.IsPathRooted(options.ResourcesPath)
                ? options.ResourcesPath
                : Path.Combine(AppContext.BaseDirectory, options.ResourcesPath);

            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
                {
                    if (CultureOf(Path.GetFileName(file)) is not { } culture)
                        continue;

                    using FileStream stream = File.OpenRead(file);
                    Load(cultures, culture, stream, file);
                }
            }
        }

        _cultures = cultures.ToFrozenDictionary(
            p => p.Key,
            p => p.Value.ToFrozenDictionary(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase
        );
    }

    /// <summary>Okunan kültürler.</summary>
    public IReadOnlyCollection<string> Cultures => _cultures.Keys;

    /// <summary>Kültür zinciriyle arar: <c>tr-TR</c> → <c>tr</c> → varsayılan kültür. Bulunamazsa <see langword="null"/>.</summary>
    public string? Find(string key, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(culture);

        for (CultureInfo current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (_cultures.TryGetValue(current.Name, out FrozenDictionary<string, string>? texts) && texts.TryGetValue(key, out string? value))
                return value;
        }

        return _cultures.TryGetValue(_defaultCulture, out FrozenDictionary<string, string>? fallback) && fallback.TryGetValue(key, out string? text)
            ? text
            : null;
    }

    /// <summary>Kültürün (ve üst kültürlerinin) tüm metinleri; yakın kültür önceliklidir.</summary>
    public IReadOnlyDictionary<string, string> GetAll(CultureInfo culture, bool includeParentCultures)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        for (CultureInfo current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (_cultures.TryGetValue(current.Name, out FrozenDictionary<string, string>? texts))
            {
                foreach ((string key, string value) in texts)
                    result.TryAdd(key, value);
            }

            if (!includeParentCultures)
                break;
        }

        return result;
    }

    /// <summary><c>tr.json</c>, <c>errors.tr.json</c>, <c>Can.Core.WebApi.Localization.core.en-US.json</c> → kültür adı.</summary>
    internal static string? CultureOf(string fileName)
    {
        string name = fileName[..^".json".Length];
        int dot = name.LastIndexOf('.');
        string candidate = dot < 0 ? name : name[(dot + 1)..];

        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(candidate, predefinedOnly: true);
            return string.IsNullOrEmpty(culture.Name) ? null : culture.Name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static void Load(Dictionary<string, Dictionary<string, string>> cultures, string culture, Stream stream, string source)
    {
        if (!cultures.TryGetValue(culture, out Dictionary<string, string>? texts))
            cultures[culture] = texts = new Dictionary<string, string>(StringComparer.Ordinal);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Çeviri dosyası okunamadı: {source}", exception);
        }

        using (document)
            Flatten(document.RootElement, prefix: null, texts);
    }

    // { "product": { "not_found": "..." } } → "product.not_found"
    private static void Flatten(JsonElement element, string? prefix, Dictionary<string, string> texts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                    Flatten(property.Value, prefix is null ? property.Name : $"{prefix}.{property.Name}", texts);
                break;
            case JsonValueKind.String when prefix is not null:
                texts[prefix] = element.GetString()!;
                break;
        }
    }
}
