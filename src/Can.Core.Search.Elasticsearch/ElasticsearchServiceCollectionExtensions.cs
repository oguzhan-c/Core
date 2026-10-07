using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Search.Elasticsearch;

public static class ElasticsearchServiceCollectionExtensions
{
    /// <summary>Aramayı Elasticsearch (ya da OpenSearch) ile yapar.</summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanSearch()
    ///     .AddIndex&lt;ProductDocument&gt;("products", m =&gt; m.Text("name", "turkish"))
    ///     .UseElasticsearch(o =&gt; builder.Configuration.GetSection("Search:Elasticsearch").Bind(o));
    /// // dotnet user-secrets set "Search:Elasticsearch:ApiKey" "..."
    /// </code>
    /// </example>
    public static CanSearchBuilder UseElasticsearch(this CanSearchBuilder builder, Action<ElasticsearchOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ElasticsearchOptions();
        configure(options);
        options.Validate();

        builder.Services.AddSingleton(options);
        builder.Services
            .AddHttpClient(ElasticsearchClient.HttpClientName, http => http.Timeout = options.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });
        builder.Services.AddSingleton(sp => new ElasticsearchClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            options,
            sp.GetService<TimeProvider>()));
        return builder.UseEngine(sp => new ElasticsearchSearchEngine(sp.GetRequiredService<ElasticsearchClient>(), sp.GetRequiredService<CanSearchOptions>()));
    }
}
