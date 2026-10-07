using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Search.Tests;

public class InMemorySearchTests
{
    private static async Task<(ServiceProvider Provider, IServiceScope Scope, ISearchIndex<Product> Index)> SeededAsync(string? tenant = "t1")
    {
        ServiceProvider provider = SearchTestHost.Build();
        (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, tenant);
        await index.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        SearchBulkResult result = await index.IndexManyAsync(SearchTestHost.Catalog, TestContext.Current.CancellationToken);
        Assert.Equal(6, result.Succeeded);
        return (provider, scope, index);
    }

    [Fact]
    public async Task Text_search_matches_words_case_insensitively_and_ranks_by_score()
    {
        (ServiceProvider provider, IServiceScope scope, ISearchIndex<Product> index) = await SeededAsync();
        using (provider)
        using (scope)
        {
            SearchResult<Product> result = await index.SearchAsync(new SearchQuery { Text = "ÇAY" }, TestContext.Current.CancellationToken);

            Assert.Equal(2, result.Total);
            Assert.Equal(new[] { "1", "6" }, result.Documents.Select(p => p.Id).Order());
            Assert.All(result.Hits, h => Assert.NotNull(h.Score));
        }
    }

    [Fact]
    public async Task Operator_fuzzy_and_prefix()
    {
        (ServiceProvider provider, IServiceScope scope, ISearchIndex<Product> index) = await SeededAsync();
        using (provider)
        using (scope)
        {
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Hepsi: "sütlü" + "çikolata" yalnızca 5'te
            Assert.Equal(new[] { "5" }, (await index.SearchAsync(new SearchQuery { Text = "sütlü çikolata" }, ct)).Documents.Select(p => p.Id));

            // Herhangi biri: çikolatalar + sütlü
            Assert.Equal(2, (await index.SearchAsync(new SearchQuery { Text = "sütlü çikolata", Operator = SearchOperator.Any }, ct)).Total);

            // Yazım hatası (tek harf)
            Assert.Equal(2, (await index.SearchAsync(new SearchQuery { Text = "cikolata" }, ct)).Total);
            Assert.Equal(0, (await index.SearchAsync(new SearchQuery { Text = "cikolata", Fuzzy = false }, ct)).Total);

            // Yazarken: son kelime önek
            Assert.Equal(2, (await index.SearchAsync(new SearchQuery { Text = "çiko", Prefix = true, Fuzzy = false }, ct)).Total);

            // Yalnızca verilen alanlarda: "kakao" açıklamada, adda yok
            Assert.Equal(0, (await index.SearchAsync(new SearchQuery { Text = "kakao", Fields = { "name^3" } }, ct)).Total);
            Assert.Equal(1, (await index.SearchAsync(new SearchQuery { Text = "kakao", Fields = { "name^3", "description" } }, ct)).Total);
        }
    }

    [Fact]
    public async Task Filters_sort_and_paging()
    {
        (ServiceProvider provider, IServiceScope scope, ISearchIndex<Product> index) = await SeededAsync();
        using (provider)
        using (scope)
        {
            var query = new SearchQuery
            {
                Filters =
                {
                    SearchFilter.In("category", ["İçecek", "Şekerleme"]),
                    SearchFilter.Range("price", gte: 12.5m, lt: 43.9m),
                    SearchFilter.Equal("discontinued", true).Not(),
                    SearchFilter.Exists("description"),
                },
                Sort = { SearchSort.Desc("price") },
            };

            SearchResult<Product> result = await index.SearchAsync(query, TestContext.Current.CancellationToken);

            Assert.Equal(new[] { "6", "1" }, result.Documents.Select(p => p.Id));
            Assert.All(result.Hits, h => Assert.Null(h.Score)); // metin yok: skor yok

            query.Filters.Clear();
            query.Size = 4;
            query.Page = 1;
            SearchResult<Product> page = await index.SearchAsync(query, TestContext.Current.CancellationToken);
            Assert.Equal(6, page.Total);
            Assert.Equal(2, page.TotalPages);
            Assert.Equal(new[] { "5", "3" }, page.Documents.Select(p => p.Id)); // fiyata göre azalan: 4, 6, 2, 1 | 5, 3
        }
    }

    [Fact]
    public async Task Aggregations_and_highlights()
    {
        (ServiceProvider provider, IServiceScope scope, ISearchIndex<Product> index) = await SeededAsync();
        using (provider)
        using (scope)
        {
            SearchResult<Product> result = await index.SearchAsync(
                new SearchQuery
                {
                    Text = "çay",
                    Highlight = { "name" },
                    Aggregations = { SearchAggregation.Terms("categories", "category"), SearchAggregation.Stats("prices", "price") },
                },
                TestContext.Current.CancellationToken
            );

            AggregationResult categories = result.Aggregations["categories"];
            Assert.Equal(new[] { new AggregationBucket("İçecek", 2) }, categories.Buckets);

            AggregationStats stats = result.Aggregations["prices"].Stats!;
            Assert.Equal(2L, stats.Count);
            Assert.Equal(18d, stats.Min);
            Assert.Equal(25d, stats.Max);
            Assert.Equal(43d, stats.Sum);

            Assert.Equal("Yeşil <em>Çay</em>", result.Hits.Single(h => h.Document.Id == "6").Highlights["name"][0]);
        }
    }

    [Fact]
    public async Task Tenants_cannot_see_or_change_each_others_documents()
    {
        using ServiceProvider provider = SearchTestHost.Build();
        CancellationToken ct = TestContext.Current.CancellationToken;

        (IServiceScope s1, ISearchIndex<Product> t1) = SearchTestHost.Index(provider, "t1");
        (IServiceScope s2, ISearchIndex<Product> t2) = SearchTestHost.Index(provider, "t2");
        (IServiceScope sh, ISearchIndex<Product> host) = SearchTestHost.Index(provider, null);
        using (s1)
        using (s2)
        using (sh)
        {
            await t1.IndexAsync(SearchTestHost.Catalog[0], ct);
            await t2.IndexAsync(SearchTestHost.Catalog[0] with { Name = "Başka mağazanın çayı" }, ct);

            Assert.Equal("Chai Çayı", (await t1.GetAsync("1", ct))!.Name);
            Assert.Equal("Başka mağazanın çayı", (await t2.GetAsync("1", ct))!.Name);
            Assert.Null(await host.GetAsync("1", ct));
            Assert.Null(await host.GetAsync("t1:1", ct)); // önekli kimlik tahmini işe yaramaz

            Assert.Equal(1, (await t2.SearchAsync(new SearchQuery { Text = "çayı" }, ct)).Total);
            Assert.Equal(0, (await host.SearchAsync(new SearchQuery(), ct)).Total);

            Assert.False(await host.DeleteAsync("1", ct));
            Assert.Equal(1, await t2.DeleteAllAsync(ct));
            Assert.NotNull(await t1.GetAsync("1", ct));
            Assert.True(await t1.DeleteAsync("1", ct));
            Assert.Null(await t1.GetAsync("1", ct));
        }
    }

    [Fact]
    public async Task Indexing_the_same_id_replaces_the_document()
    {
        (ServiceProvider provider, IServiceScope scope, ISearchIndex<Product> index) = await SeededAsync();
        using (provider)
        using (scope)
        {
            await index.IndexAsync(SearchTestHost.Catalog[0] with { Price = 99 }, TestContext.Current.CancellationToken);

            Assert.Equal(99m, (await index.GetAsync("1", TestContext.Current.CancellationToken))!.Price);
            Assert.Equal(6, (await index.SearchAsync(new SearchQuery(), TestContext.Current.CancellationToken)).Total);
        }
    }

    [Fact]
    public void Unregistered_document_type_fails_with_a_clear_message()
    {
        using ServiceProvider provider = SearchTestHost.Build();
        using IServiceScope scope = provider.CreateScope();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ISearchIndex<Unregistered>>());
        Assert.Contains("AddIndex<Unregistered>", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_size_is_limited()
    {
        (ServiceProvider provider, IServiceScope scope, ISearchIndex<Product> index) = await SeededAsync();
        using (provider)
        using (scope)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => index.SearchAsync(new SearchQuery { Size = SearchQuery.MaxSize + 1 }, TestContext.Current.CancellationToken));

            // Elasticsearch'ün max_result_window sınırı: 10. sayfa × 1000 = 10.000 olur, 11. sayfa olmaz.
            await index.SearchAsync(new SearchQuery { Page = 9, Size = 1000 }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => index.SearchAsync(new SearchQuery { Page = 10, Size = 1000 }, TestContext.Current.CancellationToken));
        }
    }

    public sealed record Unregistered(string Id) : ISearchDocument;
}
