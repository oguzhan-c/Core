using Can.Core.Search.Elasticsearch;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Search.Tests;

/// <summary>
/// Gerçek sunucuya karşı: <c>CAN_ELASTICSEARCH_URL=http://localhost:9200</c> verilmezse atlanır.
/// <c>docker compose up -d elasticsearch</c> (samples/Northwind) ile çalıştırılabilir.
/// </summary>
public class ElasticsearchIntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CAN_ELASTICSEARCH_URL");

    [Fact]
    public async Task Round_trip_against_a_real_server()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Url), "CAN_ELASTICSEARCH_URL verilmedi.");
        CancellationToken ct = TestContext.Current.CancellationToken;

        string prefix = $"can-test-{Guid.NewGuid():N}-";
        using ServiceProvider provider = SearchTestHost.Build(b => b.UseElasticsearch(o =>
        {
            o.Url = new Uri(Url!);
            o.IndexPrefix = prefix;
            o.Refresh = ElasticsearchRefresh.WaitFor;
        }));

        SearchIndexDefinition definition = provider.GetRequiredService<SearchIndexRegistry>().Get(typeof(Product));
        ISearchEngine engine = provider.GetRequiredService<ISearchEngine>();
        try
        {
            await provider.EnsureCanSearchIndexesAsync(ct);

            (IServiceScope s1, ISearchIndex<Product> t1) = SearchTestHost.Index(provider, "t1");
            (IServiceScope s2, ISearchIndex<Product> t2) = SearchTestHost.Index(provider, "t2");
            using (s1)
            using (s2)
            {
                Assert.False((await t1.IndexManyAsync(SearchTestHost.Catalog, ct)).HasFailures);
                await t2.IndexAsync(SearchTestHost.Catalog[0], ct);

                SearchResult<Product> result = await t1.SearchAsync(
                    new SearchQuery
                    {
                        Text = "cikolata", // yazım hatası
                        Highlight = { "name" },
                        Aggregations = { SearchAggregation.Terms("categories", "category") },
                    },
                    ct
                );
                Assert.Equal(2, result.Total);
                Assert.All(result.Hits, h => Assert.Contains("<em>", h.Highlights["name"][0], StringComparison.Ordinal));
                Assert.Equal("Şekerleme", result.Aggregations["categories"].Buckets.Single().Key);

                SearchResult<Product> filtered = await t1.SearchAsync(
                    new SearchQuery { Filters = { SearchFilter.Range("price", lte: 18) }, Sort = { SearchSort.Asc("price") } },
                    ct
                );
                Assert.Equal(new[] { "3", "5", "1" }, filtered.Documents.Select(p => p.Id));

                Assert.Equal(1, (await t2.SearchAsync(new SearchQuery(), ct)).Total); // tenant yalıtımı
                Assert.Equal(6, await t1.DeleteAllAsync(ct));
                Assert.NotNull(await t2.GetAsync("1", ct));
            }
        }
        finally
        {
            await engine.DropIndexAsync(definition, ct);
        }
    }
}
