using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Search;

public sealed class CanSearchOptions
{
    /// <summary>
    /// Açıksa (varsayılan) her belgeye aktif tenant yazılır ve aramalar o tenant'a daraltılır; tenant yoksa yalnızca
    /// tenant'sız belgeler görünür. Tek dizini tüm tenant'lar paylaşır.
    /// </summary>
    public bool TenantIsolation { get; set; } = true;

    /// <summary>Tenant'ın yazıldığı alan (belgenin kendi alanlarıyla çakışmayacak bir ad).</summary>
    public string TenantField { get; set; } = "canTenant";

    /// <summary>Belgelerin JSON ayarları (varsayılan: camelCase).</summary>
    public JsonSerializerOptions JsonOptions { get; set; } = new(JsonSerializerDefaults.Web);
}

/// <summary>Dizin ve motor kaydı.</summary>
public sealed class CanSearchBuilder
{
    internal CanSearchBuilder(IServiceCollection services, SearchIndexRegistry registry)
    {
        Services = services;
        Registry = registry;
    }

    public IServiceCollection Services { get; }

    internal SearchIndexRegistry Registry { get; }

    /// <summary>Belge tipi için dizin tanımlar; <see cref="ISearchIndex{TDocument}"/> enjekte edilebilir olur.</summary>
    public CanSearchBuilder AddIndex<TDocument>(string name, Action<SearchMappingBuilder>? mapping = null)
        where TDocument : class, ISearchDocument
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var builder = new SearchMappingBuilder();
        mapping?.Invoke(builder);
        Registry.Add(builder.Build(name.Trim().ToLowerInvariant(), typeof(TDocument)));
        return this;
    }

    /// <summary>Motoru değiştirir (sağlayıcı paketleri kullanır: <c>UseElasticsearch</c>).</summary>
    public CanSearchBuilder UseEngine(Func<IServiceProvider, ISearchEngine> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.RemoveAll<ISearchEngine>();
        Services.AddSingleton(factory);
        return this;
    }
}

public static class SearchServiceCollectionExtensions
{
    /// <summary>
    /// Arama altyapısı. Motor verilmezse bellek içi motor kullanılır (geliştirme/test; uygulama kapanınca silinir).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanSearch()
    ///     .AddIndex&lt;ProductDocument&gt;("products", m =&gt; m.Text("name", "turkish").Keyword("categoryName").Double("unitPrice"))
    ///     .UseElasticsearch(o =&gt; builder.Configuration.GetSection("Search:Elasticsearch").Bind(o));
    ///
    /// await app.Services.EnsureCanSearchIndexesAsync(); // dizinleri oluştur
    /// </code>
    /// </example>
    public static CanSearchBuilder AddCanSearch(this IServiceCollection services, Action<CanSearchOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.FirstOrDefault(d => d.ServiceType == typeof(SearchIndexRegistry))?.ImplementationInstance is not SearchIndexRegistry registry)
        {
            registry = new SearchIndexRegistry();
            services.AddSingleton(registry);
        }

        var options = services.FirstOrDefault(d => d.ServiceType == typeof(CanSearchOptions))?.ImplementationInstance as CanSearchOptions;
        if (options is null)
        {
            options = new CanSearchOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);

        services.TryAddSingleton<ISearchEngine, InMemorySearchEngine>();
        services.TryAdd(ServiceDescriptor.Scoped(typeof(ISearchIndex<>), typeof(SearchIndex<>)));
        return new CanSearchBuilder(services, registry);
    }

    /// <summary>Kayıtlı tüm dizinleri (yoksa) oluşturur. Uygulama açılışında bir kez çağır.</summary>
    public static async Task EnsureCanSearchIndexesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registry = (SearchIndexRegistry)services.GetService(typeof(SearchIndexRegistry))!;
        var engine = (ISearchEngine)services.GetService(typeof(ISearchEngine))!;
        var options = (CanSearchOptions)services.GetService(typeof(CanSearchOptions))!;

        SearchTenantScope scope = options.TenantIsolation ? new SearchTenantScope(true, null, options.TenantField) : SearchTenantScope.None;
        foreach (SearchIndexDefinition index in registry.All)
            await engine.EnsureIndexAsync(index, scope, cancellationToken).ConfigureAwait(false);
    }
}
