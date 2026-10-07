using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Can.Core.Search.Elasticsearch;

/// <summary>
/// Elasticsearch 8/9 ve OpenSearch 2+ için <see cref="ISearchEngine"/>. Sorgular Query DSL'e çevrilir: metin
/// <c>multi_match</c> (bulanık), filtreler <c>bool.filter</c> (skoru etkilemez, önbelleğe alınır), tenant da bir filtredir.
/// </summary>
public sealed class ElasticsearchSearchEngine : ISearchEngine
{
    private readonly ElasticsearchClient _client;
    private readonly JsonSerializerOptions _jsonOptions;

    public ElasticsearchSearchEngine(ElasticsearchClient client, CanSearchOptions searchOptions)
    {
        ArgumentNullException.ThrowIfNull(searchOptions);
        _client = client;
        _jsonOptions = searchOptions.JsonOptions;
    }

    private ElasticsearchOptions Options => _client.Options;

    public async Task EnsureIndexAsync(SearchIndexDefinition index, SearchTenantScope scope, CancellationToken cancellationToken = default)
    {
        string name = Options.IndexName(index);
        ElasticsearchResponse exists = await _client.SendAsync(HttpMethod.Head, $"/{name}", null, cancellationToken, HttpStatusCode.NotFound).ConfigureAwait(false);
        if (exists.StatusCode != HttpStatusCode.NotFound)
            return;

        try
        {
            await _client.SendAsync(HttpMethod.Put, $"/{name}", CreateIndexBody(index, scope), cancellationToken).ConfigureAwait(false);
        }
        catch (SearchException ex) when (ex.ErrorType == "resource_already_exists_exception")
        {
            // başka bir sunucu aynı anda oluşturdu
        }
    }

    public async Task<bool> DropIndexAsync(SearchIndexDefinition index, CancellationToken cancellationToken = default)
    {
        ElasticsearchResponse response = await _client
            .SendAsync(HttpMethod.Delete, $"/{Options.IndexName(index)}", null, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    public async Task<SearchBulkResult> IndexAsync(
        SearchIndexDefinition index,
        SearchTenantScope scope,
        IReadOnlyList<SearchDocumentSource> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
            return new SearchBulkResult(0, []);

        string name = Options.IndexName(index);
        var pending = new Dictionary<string, (string Id, JsonObject Source)>(StringComparer.Ordinal); // dizin kimliği → belge
        foreach (SearchDocumentSource document in documents)
        {
            var source = (JsonObject)document.Source.DeepClone();
            if (scope.Isolated)
                source[scope.Field] = scope.TenantId;
            pending[scope.DocumentId(document.Id)] = (document.Id, source);
        }

        var failures = new List<SearchBulkFailure>();
        int succeeded = 0;

        // Küme meşgulse (429, es_rejected_execution_exception) yalnızca o belgeler bekleyip tekrar gönderilir;
        // diğer hatalar kalıcıdır (eşleme hatası vb.) ve sonuçta döner. Elastic'in BulkAll yardımcısındaki kural.
        for (int attempt = 0; pending.Count > 0; attempt++)
        {
            var lines = new List<JsonNode>(pending.Count * 2);
            foreach (KeyValuePair<string, (string Id, JsonObject Source)> entry in pending)
            {
                lines.Add(new JsonObject { ["index"] = new JsonObject { ["_index"] = name, ["_id"] = entry.Key } });
                lines.Add(entry.Value.Source.DeepClone());
            }

            ElasticsearchResponse response = await _client.SendNdJsonAsync($"/_bulk?refresh={Options.RefreshParameter}", lines, cancellationToken).ConfigureAwait(false);

            var retry = new Dictionary<string, (string Id, JsonObject Source)>(StringComparer.Ordinal);
            foreach (JsonNode? item in response.Body?["items"]?.AsArray() ?? new JsonArray())
            {
                JsonNode? result = item?["index"];
                string id = result?["_id"]?.GetValue<string>() ?? string.Empty;
                if (result?["error"] is not JsonObject error)
                {
                    succeeded++;
                    continue;
                }

                if (result["status"]?.GetValue<int>() == 429 && attempt < Options.BulkRetries && pending.TryGetValue(id, out var document))
                    retry[id] = document;
                else
                    failures.Add(new SearchBulkFailure(pending.TryGetValue(id, out var failed) ? failed.Id : id, $"{error["type"]}: {error["reason"]}"));
            }

            pending = retry;
            if (pending.Count > 0)
                await Task.Delay(Options.BulkRetryDelay * Math.Pow(2, attempt), cancellationToken).ConfigureAwait(false);
        }

        return new SearchBulkResult(succeeded, failures);
    }

    public async Task<JsonObject?> GetAsync(SearchIndexDefinition index, SearchTenantScope scope, string id, CancellationToken cancellationToken = default)
    {
        ElasticsearchResponse response = await _client
            .SendAsync(HttpMethod.Get, $"/{Options.IndexName(index)}/_doc/{Uri.EscapeDataString(scope.DocumentId(id))}", null, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);

        return response.Body?["found"]?.GetValue<bool>() == true && response.Body["_source"] is JsonObject source && InScope(source, scope)
            ? (JsonObject)source.DeepClone()
            : null;
    }

    public async Task<bool> DeleteAsync(SearchIndexDefinition index, SearchTenantScope scope, string id, CancellationToken cancellationToken = default)
    {
        ElasticsearchResponse response = await _client
            .SendAsync(
                HttpMethod.Delete,
                $"/{Options.IndexName(index)}/_doc/{Uri.EscapeDataString(scope.DocumentId(id))}?refresh={Options.RefreshParameter}",
                null,
                cancellationToken,
                HttpStatusCode.NotFound
            )
            .ConfigureAwait(false);

        return response.Body?["result"]?.GetValue<string>() == "deleted";
    }

    public async Task<long> DeleteAllAsync(SearchIndexDefinition index, SearchTenantScope scope, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["query"] = new JsonObject { ["bool"] = BoolQuery(scope, null, []) } };
        ElasticsearchResponse response = await _client
            .SendAsync(HttpMethod.Post, $"/{Options.IndexName(index)}/_delete_by_query?refresh=true&conflicts=proceed", body, cancellationToken, HttpStatusCode.NotFound)
            .ConfigureAwait(false);

        return response.Body?["deleted"]?.GetValue<long>() ?? 0;
    }

    public async Task<SearchEngineResult> SearchAsync(SearchIndexDefinition index, SearchTenantScope scope, SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ElasticsearchResponse response = await _client
            .SendAsync(HttpMethod.Post, $"/{Options.IndexName(index)}/_search", BuildSearch(scope, query, _jsonOptions), cancellationToken)
            .ConfigureAwait(false);

        return ReadSearch(response.Body);
    }

    // ---------------------------------------------------------------- istek gövdeleri

    internal static JsonObject CreateIndexBody(SearchIndexDefinition index, SearchTenantScope scope)
    {
        var properties = new JsonObject();
        foreach (SearchField field in index.Fields)
            properties[field.Name] = FieldMapping(field);

        if (scope.Isolated)
            properties[scope.Field] = new JsonObject { ["type"] = "keyword" };

        var body = new JsonObject { ["mappings"] = new JsonObject { ["properties"] = properties } };

        var settings = new JsonObject();
        if (index.Shards is { } shards)
            settings["number_of_shards"] = shards;
        if (index.Replicas is { } replicas)
            settings["number_of_replicas"] = replicas;
        if (settings.Count > 0)
            body["settings"] = settings;

        return body;
    }

    private static JsonObject FieldMapping(SearchField field)
    {
        var mapping = new JsonObject
        {
            ["type"] = field.Type switch
            {
                SearchFieldType.Text => "text",
                SearchFieldType.Keyword => "keyword",
                SearchFieldType.Long => "long",
                SearchFieldType.Double => "double",
                SearchFieldType.Boolean => "boolean",
                SearchFieldType.Date => "date",
                _ => throw new ArgumentOutOfRangeException(nameof(field), field.Type, null),
            },
        };

        if (field.Type == SearchFieldType.Text)
        {
            if (!string.IsNullOrWhiteSpace(field.Analyzer))
                mapping["analyzer"] = field.Analyzer;
            if (field.WithKeyword)
                mapping["fields"] = new JsonObject { ["keyword"] = new JsonObject { ["type"] = "keyword", ["ignore_above"] = 256 } };
        }

        return mapping;
    }

    internal static JsonObject BuildSearch(SearchTenantScope scope, SearchQuery query, JsonSerializerOptions jsonOptions)
    {
        JsonObject? text = null;
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            text = new JsonObject
            {
                ["query"] = query.Text.Trim(),
                ["type"] = query.Prefix ? "bool_prefix" : "best_fields",
                ["operator"] = query.Operator == SearchOperator.All ? "and" : "or",
            };
            if (query.Fields.Count > 0)
                text["fields"] = new JsonArray(query.Fields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
            if (query.Fuzzy)
                text["fuzziness"] = "AUTO";
        }

        var body = new JsonObject
        {
            ["from"] = query.Page * query.Size,
            ["size"] = query.Size,
            ["track_total_hits"] = true,
            ["query"] = new JsonObject { ["bool"] = BoolQuery(scope, text is null ? null : new JsonObject { ["multi_match"] = text }, query.Filters, jsonOptions) },
        };

        if (query.Sort.Count > 0)
        {
            body["sort"] = new JsonArray(
                query.Sort.Select(s => (JsonNode)new JsonObject
                {
                    [s.Field] = new JsonObject { ["order"] = s.Descending ? "desc" : "asc", ["missing"] = "_last" },
                }).ToArray()
            );
        }

        if (query.Highlight.Count > 0)
        {
            var fields = new JsonObject();
            foreach (string field in query.Highlight)
                fields[field] = new JsonObject();
            body["highlight"] = new JsonObject { ["fields"] = fields };
        }

        if (query.Aggregations.Count > 0)
        {
            var aggregations = new JsonObject();
            foreach (SearchAggregation aggregation in query.Aggregations)
            {
                aggregations[aggregation.Name] = aggregation.Kind switch
                {
                    SearchAggregationKind.Terms => new JsonObject
                    {
                        ["terms"] = new JsonObject { ["field"] = aggregation.Field, ["size"] = aggregation.Size },
                    },
                    _ => new JsonObject { ["stats"] = new JsonObject { ["field"] = aggregation.Field } },
                };
            }

            body["aggs"] = aggregations;
        }

        return body;
    }

    private static JsonObject BoolQuery(SearchTenantScope scope, JsonObject? must, IEnumerable<SearchFilter> filters, JsonSerializerOptions? jsonOptions = null)
    {
        var filter = new JsonArray();
        var mustNot = new JsonArray();

        if (scope.Isolated)
        {
            if (scope.TenantId is { Length: > 0 } tenant)
                filter.Add(new JsonObject { ["term"] = new JsonObject { [scope.Field] = tenant } });
            else
                mustNot.Add(new JsonObject { ["exists"] = new JsonObject { ["field"] = scope.Field } });
        }

        foreach (SearchFilter f in filters)
            (f.Negated ? mustNot : filter).Add(FilterClause(f, jsonOptions));

        var result = new JsonObject();
        if (must is not null)
            result["must"] = new JsonArray(must);
        if (filter.Count > 0)
            result["filter"] = filter;
        if (mustNot.Count > 0)
            result["must_not"] = mustNot;
        return result;
    }

    private static JsonObject FilterClause(SearchFilter filter, JsonSerializerOptions? jsonOptions)
    {
        switch (filter.Kind)
        {
            case SearchFilterKind.Equal:
                return new JsonObject { ["term"] = new JsonObject { [filter.Field] = Value(filter.Values[0], jsonOptions) } };
            case SearchFilterKind.In:
                return new JsonObject { ["terms"] = new JsonObject { [filter.Field] = new JsonArray(filter.Values.Select(v => Value(v, jsonOptions)).ToArray()) } };
            case SearchFilterKind.Exists:
                return new JsonObject { ["exists"] = new JsonObject { ["field"] = filter.Field } };
            default:
                var range = new JsonObject();
                if (filter.GreaterThanOrEqual is { } gte)
                    range["gte"] = Value(gte, jsonOptions);
                if (filter.GreaterThan is { } gt)
                    range["gt"] = Value(gt, jsonOptions);
                if (filter.LessThanOrEqual is { } lte)
                    range["lte"] = Value(lte, jsonOptions);
                if (filter.LessThan is { } lt)
                    range["lt"] = Value(lt, jsonOptions);
                return new JsonObject { ["range"] = new JsonObject { [filter.Field] = range } };
        }
    }

    private static JsonNode? Value(object value, JsonSerializerOptions? jsonOptions) =>
        value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value, value.GetType(), jsonOptions);

    // ---------------------------------------------------------------- yanıt

    internal static SearchEngineResult ReadSearch(JsonNode? body)
    {
        JsonNode? hitsNode = body?["hits"];
        long total = hitsNode?["total"] switch
        {
            JsonObject t => t["value"]?.GetValue<long>() ?? 0,
            JsonValue v => v.GetValue<long>(),
            _ => 0,
        };

        var hits = new List<SearchEngineHit>();
        foreach (JsonNode? hit in hitsNode?["hits"]?.AsArray() ?? new JsonArray())
        {
            if (hit?["_source"] is not JsonObject source)
                continue;

            double? score = hit["_score"] is JsonValue s && s.GetValueKind() == JsonValueKind.Number ? s.GetValue<double>() : null;
            var highlights = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (hit["highlight"] is JsonObject highlight)
            {
                foreach (KeyValuePair<string, JsonNode?> field in highlight)
                    highlights[field.Key] = field.Value?.AsArray().Select(f => f?.GetValue<string>() ?? string.Empty).ToArray() ?? [];
            }

            hits.Add(new SearchEngineHit((JsonObject)source.DeepClone(), score, highlights));
        }

        var aggregations = new Dictionary<string, AggregationResult>(StringComparer.Ordinal);
        if (body?["aggregations"] is JsonObject aggs)
        {
            foreach (KeyValuePair<string, JsonNode?> aggregation in aggs)
            {
                if (aggregation.Value is not JsonObject value)
                    continue;

                if (value["buckets"] is JsonArray buckets)
                {
                    aggregations[aggregation.Key] = new AggregationResult(
                        buckets
                            .OfType<JsonObject>()
                            .Select(b => new AggregationBucket(
                                b["key_as_string"]?.GetValue<string>() ?? KeyText(b["key"]),
                                b["doc_count"]?.GetValue<long>() ?? 0))
                            .ToArray(),
                        null
                    );
                }
                else
                {
                    aggregations[aggregation.Key] = new AggregationResult(
                        [],
                        new AggregationStats(
                            value["count"]?.GetValue<long>() ?? 0,
                            Number(value["min"]),
                            Number(value["max"]),
                            Number(value["avg"]),
                            Number(value["sum"]) ?? 0
                        )
                    );
                }
            }
        }

        return new SearchEngineResult(hits, total, aggregations);
    }

    private static string KeyText(JsonNode? key) =>
        key is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : key?.ToJsonString() ?? string.Empty;

    private static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static bool InScope(JsonObject source, SearchTenantScope scope) =>
        !scope.Isolated || string.Equals(source[scope.Field]?.GetValue<string>(), scope.TenantId, StringComparison.Ordinal);
}
