using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Can.Core.Search;

/// <summary>
/// Bellek içi arama motoru: geliştirme ve testler için (kalıcı değil, tek süreç). Kelime eşleşmesi basittir: küçük
/// harfe çevrilmiş kelimeler metin alanlarında aranır, yazım hatası toleransı tek harf farkıdır. Gerçek alaka
/// sıralaması, kök bulma (stemming) ve dil çözümleyicileri için Elasticsearch kullan.
/// </summary>
public sealed class InMemorySearchEngine : ISearchEngine
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JsonObject>> _indexes = new(StringComparer.Ordinal);

    public Task EnsureIndexAsync(SearchIndexDefinition index, SearchTenantScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        _indexes.GetOrAdd(index.Name, _ => new ConcurrentDictionary<string, JsonObject>(StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    public Task<bool> DropIndexAsync(SearchIndexDefinition index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        return Task.FromResult(_indexes.TryRemove(index.Name, out _));
    }

    public Task<SearchBulkResult> IndexAsync(
        SearchIndexDefinition index,
        SearchTenantScope scope,
        IReadOnlyList<SearchDocumentSource> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ConcurrentDictionary<string, JsonObject> store = Store(index);

        foreach (SearchDocumentSource document in documents)
        {
            var source = (JsonObject)document.Source.DeepClone();
            if (scope.Isolated)
                source[scope.Field] = scope.TenantId;
            store[scope.DocumentId(document.Id)] = source;
        }

        return Task.FromResult(new SearchBulkResult(documents.Count, []));
    }

    public Task<JsonObject?> GetAsync(SearchIndexDefinition index, SearchTenantScope scope, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Store(index).TryGetValue(scope.DocumentId(id), out JsonObject? source) && InScope(source, scope) ? (JsonObject)source.DeepClone() : null);

    public Task<bool> DeleteAsync(SearchIndexDefinition index, SearchTenantScope scope, string id, CancellationToken cancellationToken = default)
    {
        ConcurrentDictionary<string, JsonObject> store = Store(index);
        string key = scope.DocumentId(id);
        return Task.FromResult(store.TryGetValue(key, out JsonObject? source) && InScope(source, scope) && store.TryRemove(key, out _));
    }

    public Task<long> DeleteAllAsync(SearchIndexDefinition index, SearchTenantScope scope, CancellationToken cancellationToken = default)
    {
        ConcurrentDictionary<string, JsonObject> store = Store(index);
        long deleted = 0;
        foreach (KeyValuePair<string, JsonObject> entry in store.Where(e => InScope(e.Value, scope)).ToList())
        {
            if (store.TryRemove(entry.Key, out _))
                deleted++;
        }

        return Task.FromResult(deleted);
    }

    public Task<SearchEngineResult> SearchAsync(SearchIndexDefinition index, SearchTenantScope scope, SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string[] terms = Tokenize(query.Text);
        string[] fields = query.Fields.Select(f => f.Split('^')[0]).ToArray();

        var matches = new List<(JsonObject Source, double Score)>();
        foreach (JsonObject source in Store(index).Values)
        {
            if (!InScope(source, scope) || !query.Filters.All(f => Matches(source, f)))
                continue;

            double score = terms.Length == 0 ? 1 : Score(source, fields, terms, query);
            if (score > 0)
                matches.Add((source, score));
        }

        IEnumerable<(JsonObject Source, double Score)> ordered = query.Sort.Count == 0
            ? matches.OrderByDescending(m => m.Score)
            : Sort(matches, query.Sort);

        var aggregations = query.Aggregations.ToDictionary(a => a.Name, a => Aggregate(matches.Select(m => m.Source), a), StringComparer.Ordinal);

        SearchEngineHit[] hits = ordered
            .Skip(query.Page * query.Size)
            .Take(query.Size)
            .Select(m => new SearchEngineHit(
                (JsonObject)m.Source.DeepClone(),
                terms.Length == 0 ? null : m.Score,
                Highlights(m.Source, query.Highlight, terms)))
            .ToArray();

        return Task.FromResult(new SearchEngineResult(hits, matches.Count, aggregations));
    }

    // ---------------------------------------------------------------- yardımcılar

    private ConcurrentDictionary<string, JsonObject> Store(SearchIndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return _indexes.GetOrAdd(index.Name, _ => new ConcurrentDictionary<string, JsonObject>(StringComparer.Ordinal));
    }

    private static bool InScope(JsonObject source, SearchTenantScope scope) =>
        !scope.Isolated || string.Equals(source[scope.Field]?.GetValue<string>(), scope.TenantId, StringComparison.Ordinal);

    private static string[] Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text
                .ToLowerInvariant()
                .Replace("\u0307", "", StringComparison.Ordinal) // "İ" → "i̇" → "i"
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double Score(JsonObject source, string[] fields, string[] terms, SearchQuery query)
    {
        IEnumerable<string> texts = fields.Length == 0 ? AllStrings(source) : fields.SelectMany(f => Values(source, f)).Select(ToText).OfType<string>();
        string[] words = texts.SelectMany(t => Tokenize(t)).ToArray();

        double score = 0;
        int matched = 0;
        for (int i = 0; i < terms.Length; i++)
        {
            bool prefix = query.Prefix && i == terms.Length - 1;
            int hits = words.Count(w => w == terms[i] || (prefix && w.StartsWith(terms[i], StringComparison.Ordinal)) || (query.Fuzzy && OneEditApart(w, terms[i])));
            if (hits > 0)
            {
                matched++;
                score += hits;
            }
        }

        bool ok = query.Operator == SearchOperator.All ? matched == terms.Length : matched > 0;
        return ok ? score : 0;
    }

    /// <summary>En fazla bir harf eklenmiş/silinmiş/değişmiş (kısa kelimelerde tolerans yok).</summary>
    private static bool OneEditApart(string a, string b)
    {
        if (b.Length < 4 || Math.Abs(a.Length - b.Length) > 1 || a == b)
            return false;

        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }

            if (++edits > 1)
                return false;
            if (a.Length > b.Length)
                i++;
            else if (a.Length < b.Length)
                j++;
            else
            {
                i++;
                j++;
            }
        }

        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }

    private static IEnumerable<string> AllStrings(JsonNode? node) =>
        node switch
        {
            JsonObject obj => obj.SelectMany(p => AllStrings(p.Value)),
            JsonArray array => array.SelectMany(AllStrings),
            JsonValue value when value.GetValueKind() == JsonValueKind.String => [value.GetValue<string>()],
            _ => [],
        };

    /// <summary>Noktalı yol (<c>category.name</c>); diziler açılır. <c>.keyword</c> eki yok sayılır.</summary>
    private static IEnumerable<JsonNode> Values(JsonNode? node, string path)
    {
        if (path.EndsWith(".keyword", StringComparison.Ordinal))
            path = path[..^".keyword".Length];

        IEnumerable<JsonNode?> current = [node];
        foreach (string part in path.Split('.'))
            current = current.SelectMany(n => Expand(n is JsonObject o ? FindProperty(o, part) : null));

        return current.SelectMany(Expand).OfType<JsonNode>();

        // Dizi zaten dizindeki düğümdür; tek değer yeni bir JsonArray'e konmaz (düğümün ebeveyni değişirdi).
        static IEnumerable<JsonNode?> Expand(JsonNode? n) => n is JsonArray array ? array : new[] { n };
    }

    private static JsonNode? FindProperty(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out JsonNode? value)
            ? value
            : obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? ToText(JsonNode? node) =>
        node is JsonValue value ? value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString(),
            _ => null,
        } : null;

    private static bool Matches(JsonObject source, SearchFilter filter)
    {
        JsonNode[] values = Values(source, filter.Field).Where(v => v is JsonValue jv && jv.GetValueKind() != JsonValueKind.Null).ToArray();
        bool result = filter.Kind switch
        {
            SearchFilterKind.Exists => values.Length > 0,
            SearchFilterKind.Equal or SearchFilterKind.In => values.Any(v => filter.Values.Any(expected => Compare(v, expected) == 0)),
            SearchFilterKind.Range => values.Any(v =>
                (filter.GreaterThanOrEqual is null || Compare(v, filter.GreaterThanOrEqual) >= 0)
                && (filter.GreaterThan is null || Compare(v, filter.GreaterThan) > 0)
                && (filter.LessThanOrEqual is null || Compare(v, filter.LessThanOrEqual) <= 0)
                && (filter.LessThan is null || Compare(v, filter.LessThan) < 0)),
            _ => false,
        };

        return filter.Negated ? !result : result;
    }

    /// <summary>Sayılar sayı olarak, tarihler tarih olarak, gerisi metin olarak karşılaştırılır. Karşılaştırılamazsa 2.</summary>
    private static int Compare(JsonNode node, object expected)
    {
        JsonNode? other = expected as JsonNode ?? JsonSerializer.SerializeToNode(expected);
        string? left = ToText(node);
        string? right = ToText(other);
        if (left is null || right is null)
            return 2;

        if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            && double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            return a.CompareTo(b);

        if (DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d1)
            && DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset d2))
            return d1.CompareTo(d2);

        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static IEnumerable<(JsonObject Source, double Score)> Sort(List<(JsonObject Source, double Score)> matches, IList<SearchSort> sorts)
    {
        var comparer = Comparer<(JsonObject Source, double Score)>.Create((x, y) =>
        {
            foreach (SearchSort sort in sorts)
            {
                JsonNode? a = Values(x.Source, sort.Field).FirstOrDefault();
                JsonNode? b = Values(y.Source, sort.Field).FirstOrDefault();
                int result = (a, b) switch
                {
                    (null, null) => 0,
                    (null, _) => 1, // eksik değerler sonda
                    (_, null) => -1,
                    _ => Math.Clamp(Compare(a, b), -1, 1) * (sort.Descending ? -1 : 1),
                };
                if (result != 0)
                    return result;
            }

            return 0;
        });

        return matches.Order(comparer);
    }

    private static AggregationResult Aggregate(IEnumerable<JsonObject> sources, SearchAggregation aggregation)
    {
        string[] values = sources.SelectMany(s => Values(s, aggregation.Field)).Select(ToText).OfType<string>().ToArray();

        if (aggregation.Kind == SearchAggregationKind.Stats)
        {
            double[] numbers = values
                .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? (double?)n : null)
                .OfType<double>()
                .ToArray();
            return new AggregationResult(
                [],
                new AggregationStats(numbers.Length, numbers.Length == 0 ? null : numbers.Min(), numbers.Length == 0 ? null : numbers.Max(), numbers.Length == 0 ? null : numbers.Average(), numbers.Sum())
            );
        }

        AggregationBucket[] buckets = values
            .GroupBy(v => v, StringComparer.Ordinal)
            .Select(g => new AggregationBucket(g.Key, g.LongCount()))
            .OrderByDescending(b => b.Count)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .Take(aggregation.Size)
            .ToArray();
        return new AggregationResult(buckets, null);
    }

    private static Dictionary<string, IReadOnlyList<string>> Highlights(JsonObject source, IList<string> fields, string[] terms)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (terms.Length == 0)
            return result;

        foreach (string field in fields)
        {
            string[] marked = Values(source, field)
                .Select(ToText)
                .OfType<string>()
                .Where(text => terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Select(text => terms.Aggregate(text, (current, term) => Mark(current, term)))
                .ToArray();
            if (marked.Length > 0)
                result[field] = marked;
        }

        return result;
    }

    private static string Mark(string text, string term)
    {
        int index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? text : $"{text[..index]}<em>{text.Substring(index, term.Length)}</em>{text[(index + term.Length)..]}";
    }
}
