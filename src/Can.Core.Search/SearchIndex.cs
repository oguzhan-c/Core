using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Can.Core.MultiTenancy;

namespace Can.Core.Search;

/// <summary>
/// Bir belge tipinin arama dizini. Scoped: aktif tenant'a göre çalışır (tenant yalıtımı açıksa bir tenant diğerinin
/// belgelerini göremez, değiştiremez).
/// </summary>
/// <example>
/// <code>
/// await products.IndexAsync(new ProductDocument(p.Id.ToString(), p.Name, p.UnitPrice), ct);
/// SearchResult&lt;ProductDocument&gt; result = await products.SearchAsync(new SearchQuery { Text = "çay" }, ct);
/// </code>
/// </example>
public interface ISearchIndex<TDocument>
    where TDocument : class, ISearchDocument
{
    SearchIndexDefinition Definition { get; }

    /// <summary>Dizin yoksa oluşturur (uygulama açılışında <c>EnsureCanSearchIndexesAsync</c> hepsini yapar).</summary>
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);

    /// <summary>Belgeyi ekler ya da (aynı kimlik varsa) değiştirir.</summary>
    Task IndexAsync(TDocument document, CancellationToken cancellationToken = default);

    /// <summary>Toplu ekleme/değiştirme (yeniden dizinleme). Başarısız belgeler sonuçta döner, exception atılmaz.</summary>
    Task<SearchBulkResult> IndexManyAsync(IEnumerable<TDocument> documents, CancellationToken cancellationToken = default);

    Task<TDocument?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Bu tenant'ın (yalıtım kapalıysa dizindeki) tüm belgelerini siler.</summary>
    Task<long> DeleteAllAsync(CancellationToken cancellationToken = default);

    Task<SearchResult<TDocument>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
}

internal sealed class SearchIndex<TDocument> : ISearchIndex<TDocument>
    where TDocument : class, ISearchDocument
{
    private const int BulkBatchSize = 500;

    private readonly ISearchEngine _engine;
    private readonly CanSearchOptions _options;
    private readonly SearchTenantScope _scope;

    public SearchIndex(SearchIndexRegistry registry, ISearchEngine engine, CanSearchOptions options, IServiceProvider services)
    {
        Definition = registry.Get(typeof(TDocument));
        _engine = engine;
        _options = options;
        _scope = options.TenantIsolation
            ? new SearchTenantScope(true, (services.GetService(typeof(TenantContext)) as TenantContext)?.TenantId, options.TenantField)
            : SearchTenantScope.None;
    }

    public SearchIndexDefinition Definition { get; }

    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) => _engine.EnsureIndexAsync(Definition, _scope, cancellationToken);

    public async Task IndexAsync(TDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        using Activity? activity = SearchTelemetry.Start("index", Definition.Name);

        SearchBulkResult result = await _engine.IndexAsync(Definition, _scope, [ToSource(document)], cancellationToken).ConfigureAwait(false);
        if (result.HasFailures)
            throw new SearchException($"'{document.Id}' dizine yazılamadı: {result.Failures[0].Reason}");
    }

    public async Task<SearchBulkResult> IndexManyAsync(IEnumerable<TDocument> documents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        using Activity? activity = SearchTelemetry.Start("bulk", Definition.Name);

        int succeeded = 0;
        var failures = new List<SearchBulkFailure>();
        foreach (TDocument[] batch in documents.Chunk(BulkBatchSize))
        {
            SearchBulkResult result = await _engine.IndexAsync(Definition, _scope, batch.Select(ToSource).ToArray(), cancellationToken).ConfigureAwait(false);
            succeeded += result.Succeeded;
            failures.AddRange(result.Failures);
        }

        activity?.SetTag("can.search.documents", succeeded + failures.Count);
        return new SearchBulkResult(succeeded, failures);
    }

    public async Task<TDocument?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        JsonObject? source = await _engine.GetAsync(Definition, _scope, id, cancellationToken).ConfigureAwait(false);
        return source is null ? null : FromSource(source);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _engine.DeleteAsync(Definition, _scope, id, cancellationToken);
    }

    public Task<long> DeleteAllAsync(CancellationToken cancellationToken = default) => _engine.DeleteAllAsync(Definition, _scope, cancellationToken);

    public async Task<SearchResult<TDocument>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        using Activity? activity = SearchTelemetry.Start("search", Definition.Name);

        SearchEngineResult result = await _engine.SearchAsync(Definition, _scope, query, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("can.search.total", result.Total);

        return new SearchResult<TDocument>(
            result.Hits.Select(h => new SearchHit<TDocument>(FromSource(h.Source), h.Score, h.Highlights)).ToArray(),
            result.Total,
            query.Page,
            query.Size,
            result.Aggregations
        );
    }

    private SearchDocumentSource ToSource(TDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id, nameof(document));
        JsonObject source = JsonSerializer.SerializeToNode(document, _options.JsonOptions) as JsonObject
            ?? throw new InvalidOperationException($"{typeof(TDocument).Name} bir JSON nesnesine çevrilemedi.");
        return new SearchDocumentSource(document.Id, source);
    }

    private TDocument FromSource(JsonObject source)
    {
        if (_scope.Isolated)
            source.Remove(_scope.Field);

        return source.Deserialize<TDocument>(_options.JsonOptions)
            ?? throw new InvalidOperationException($"{typeof(TDocument).Name} okunamadı.");
    }
}

/// <summary>Kayıtlı dizinler (belge tipi → tanım).</summary>
public sealed class SearchIndexRegistry
{
    private readonly Dictionary<Type, SearchIndexDefinition> _indexes = [];

    public IReadOnlyCollection<SearchIndexDefinition> All => _indexes.Values;

    public SearchIndexDefinition Get(Type documentType) =>
        _indexes.TryGetValue(documentType, out SearchIndexDefinition? definition)
            ? definition
            : throw new InvalidOperationException($"{documentType.Name} için arama dizini kayıtlı değil: AddCanSearch().AddIndex<{documentType.Name}>(\"ad\").");

    internal void Add(SearchIndexDefinition definition)
    {
        if (_indexes.Values.Any(d => d.Name == definition.Name && d.DocumentType != definition.DocumentType))
            throw new InvalidOperationException($"'{definition.Name}' adlı dizin başka bir tip için kayıtlı.");
        _indexes[definition.DocumentType] = definition;
    }
}

/// <summary>Arama span'ları (<c>Can.Core.Search</c>); motorun HTTP çağrıları bunların altında görünür.</summary>
public static class SearchTelemetry
{
    public const string Name = "Can.Core.Search";

    public static readonly ActivitySource Source = new(Name);

    internal static Activity? Start(string operation, string index)
    {
        Activity? activity = Source.StartActivity($"search.{operation} {index}", ActivityKind.Internal);
        activity?.SetTag("db.system.name", "search");
        activity?.SetTag("db.operation.name", operation);
        activity?.SetTag("db.collection.name", index);
        return activity;
    }
}
