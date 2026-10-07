using System.Text.Json.Nodes;

namespace Can.Core.Search;

/// <summary>
/// İşlemin tenant kapsamı. <see cref="Isolated"/> ise belgelere <see cref="Field"/> alanı yazılır, kimlikler tenant ile
/// öneklenir ve her sorgu bu tenant'a (tenant yoksa tenant'sız belgelere) daraltılır.
/// </summary>
public readonly record struct SearchTenantScope(bool Isolated, string? TenantId, string Field)
{
    public static SearchTenantScope None => new(false, null, string.Empty);

    /// <summary>
    /// Dizindeki kimlik: <c>{tenant}:{id}</c> (tenant'sız belgeler <c>~:{id}</c>). Önek her zaman kapsamdan gelir; bu
    /// yüzden bir tenant kimliği tahmin ederek başka tenant'ın belgesine ulaşamaz.
    /// </summary>
    public string DocumentId(string id) => Isolated ? $"{(TenantId is { Length: > 0 } tenant ? tenant : "~")}:{id}" : id;
}

/// <summary>Dizine yazılacak belge (JSON).</summary>
public sealed record SearchDocumentSource(string Id, JsonObject Source);

public sealed record SearchEngineHit(JsonObject Source, double? Score, IReadOnlyDictionary<string, IReadOnlyList<string>> Highlights);

public sealed record SearchEngineResult(IReadOnlyList<SearchEngineHit> Hits, long Total, IReadOnlyDictionary<string, AggregationResult> Aggregations);

/// <summary>
/// Arama motoru (bellek içi, Elasticsearch, OpenSearch ...). Belgeleri JSON olarak alır; tiplere çevirme ve tenant kapsamı
/// <see cref="ISearchIndex{TDocument}"/>'tedir. Uygulama kodu bunu değil <see cref="ISearchIndex{TDocument}"/>'i kullanır.
/// </summary>
public interface ISearchEngine
{
    /// <summary>Dizin yoksa eşlemesiyle oluşturur; varsa dokunmaz.</summary>
    Task EnsureIndexAsync(SearchIndexDefinition index, SearchTenantScope scope, CancellationToken cancellationToken = default);

    /// <summary>Dizini siler (yeniden oluşturmak için). Yoksa <see langword="false"/>.</summary>
    Task<bool> DropIndexAsync(SearchIndexDefinition index, CancellationToken cancellationToken = default);

    /// <summary>Belgeleri ekler ya da değiştirir (aynı kimlik).</summary>
    Task<SearchBulkResult> IndexAsync(SearchIndexDefinition index, SearchTenantScope scope, IReadOnlyList<SearchDocumentSource> documents, CancellationToken cancellationToken = default);

    Task<JsonObject?> GetAsync(SearchIndexDefinition index, SearchTenantScope scope, string id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(SearchIndexDefinition index, SearchTenantScope scope, string id, CancellationToken cancellationToken = default);

    /// <summary>Kapsamdaki tüm belgeleri siler; silinen sayısı.</summary>
    Task<long> DeleteAllAsync(SearchIndexDefinition index, SearchTenantScope scope, CancellationToken cancellationToken = default);

    Task<SearchEngineResult> SearchAsync(SearchIndexDefinition index, SearchTenantScope scope, SearchQuery query, CancellationToken cancellationToken = default);
}
