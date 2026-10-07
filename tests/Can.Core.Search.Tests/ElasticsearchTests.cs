using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Can.Core.Search.Elasticsearch;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Search.Tests;

public class ElasticsearchTests
{
    private static (ServiceProvider Provider, FakeElasticsearch Server) Build(Action<ElasticsearchOptions>? configure = null)
    {
        var server = new FakeElasticsearch();
        ServiceProvider provider = SearchTestHost.Build(b =>
        {
            b.UseElasticsearch(o =>
            {
                o.Url = new Uri("http://es.test:9200");
                o.IndexPrefix = "app-";
                o.Refresh = ElasticsearchRefresh.WaitFor;
                configure?.Invoke(o);
            });
            b.Services.AddHttpClient(ElasticsearchClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => server);
        });
        return (provider, server);
    }

    [Fact]
    public async Task Creates_index_with_mapping_only_when_missing()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build();
        using (provider)
        {
            server.Respond(HttpMethod.Head, "/app-products", HttpStatusCode.NotFound);
            server.Respond(HttpMethod.Put, "/app-products", HttpStatusCode.OK, """{"acknowledged":true}""");

            await provider.EnsureCanSearchIndexesAsync(TestContext.Current.CancellationToken);

            JsonNode body = server.Requests.Single(r => r.Method == HttpMethod.Put).Json!;
            JsonNode properties = body["mappings"]!["properties"]!;
            Assert.Equal("text", properties["name"]!["type"]!.GetValue<string>());
            Assert.Equal("turkish", properties["name"]!["analyzer"]!.GetValue<string>());
            Assert.Equal("keyword", properties["name"]!["fields"]!["keyword"]!["type"]!.GetValue<string>());
            Assert.Equal("keyword", properties["category"]!["type"]!.GetValue<string>());
            Assert.Equal("double", properties["price"]!["type"]!.GetValue<string>());
            Assert.Equal("keyword", properties["canTenant"]!["type"]!.GetValue<string>());

            // Var olan dizine dokunulmaz.
            server.Requests.Clear();
            server.ClearResponses();
            server.Respond(HttpMethod.Head, "/app-products", HttpStatusCode.OK);
            await provider.EnsureCanSearchIndexesAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Put);
        }
    }

    [Fact]
    public async Task Bulk_indexing_writes_ndjson_with_tenant_scoped_ids_and_reports_failures()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build();
        using (provider)
        {
            server.Respond(HttpMethod.Post, "/_bulk?refresh=wait_for", HttpStatusCode.OK, """
                {"errors":true,"items":[
                  {"index":{"_id":"t1:1","status":201}},
                  {"index":{"_id":"t1:2","status":400,"error":{"type":"mapper_parsing_exception","reason":"failed to parse field [price]"}}}
                ]}
                """);

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, "t1");
            using (scope)
            {
                SearchBulkResult result = await index.IndexManyAsync(SearchTestHost.Catalog[..2], TestContext.Current.CancellationToken);

                Assert.Equal(1, result.Succeeded);
                SearchBulkFailure failure = Assert.Single(result.Failures);
                Assert.Equal("2", failure.Id); // önek atılmış asıl kimlik
                Assert.Contains("mapper_parsing_exception", failure.Reason, StringComparison.Ordinal);
            }

            FakeElasticsearch.Request request = server.Requests.Single();
            Assert.Equal("/_bulk?refresh=wait_for", request.PathAndQuery);
            Assert.Equal("application/x-ndjson", request.ContentType);
            string[] lines = request.Body!.TrimEnd('\n').Split('\n');
            Assert.Equal(4, lines.Length);
            Assert.Equal("app-products", JsonNode.Parse(lines[0])!["index"]!["_index"]!.GetValue<string>());
            Assert.Equal("t1:1", JsonNode.Parse(lines[0])!["index"]!["_id"]!.GetValue<string>());
            JsonNode source = JsonNode.Parse(lines[1])!;
            Assert.Equal("Chai Çayı", source["name"]!.GetValue<string>());
            Assert.Equal("t1", source["canTenant"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Search_translates_query_to_dsl_and_reads_hits_highlights_and_aggregations()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build();
        using (provider)
        {
            server.Respond(HttpMethod.Post, "/app-products/_search", HttpStatusCode.OK, """
                {
                  "hits": {
                    "total": { "value": 42, "relation": "eq" },
                    "hits": [
                      { "_id": "t1:1", "_score": 3.5,
                        "_source": { "id": "1", "name": "Chai Çayı", "category": "İçecek", "price": 18, "discontinued": false, "canTenant": "t1" },
                        "highlight": { "name": ["Chai <em>Çayı</em>"] } }
                    ]
                  },
                  "aggregations": {
                    "categories": { "buckets": [ { "key": "İçecek", "doc_count": 30 }, { "key": "Çeşni", "doc_count": 12 } ] },
                    "discontinued": { "buckets": [ { "key": 0, "key_as_string": "false", "doc_count": 40 } ] },
                    "prices": { "count": 42, "min": 2.5, "max": 263.5, "avg": 28.8, "sum": 1209.6 }
                  }
                }
                """);

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, "t1");
            using (scope)
            {
                SearchResult<Product> result = await index.SearchAsync(
                    new SearchQuery
                    {
                        Text = " çay ",
                        Fields = { "name^3", "description" },
                        Prefix = true,
                        Filters = { SearchFilter.Range("price", gte: 10, lte: 50), SearchFilter.Equal("discontinued", true).Not() },
                        Sort = { SearchSort.Desc("price") },
                        Highlight = { "name" },
                        Aggregations =
                        {
                            SearchAggregation.Terms("categories", "category", 5),
                            SearchAggregation.Terms("discontinued", "discontinued"),
                            SearchAggregation.Stats("prices", "price"),
                        },
                        Page = 2,
                        Size = 10,
                    },
                    TestContext.Current.CancellationToken
                );

                // yanıt
                Assert.Equal(42, result.Total);
                SearchHit<Product> hit = Assert.Single(result.Hits);
                Assert.Equal("Chai Çayı", hit.Document.Name);
                Assert.Equal(3.5, hit.Score);
                Assert.Equal("Chai <em>Çayı</em>", hit.Highlights["name"][0]);
                Assert.Equal(new[] { new AggregationBucket("İçecek", 30), new AggregationBucket("Çeşni", 12) }, result.Aggregations["categories"].Buckets);
                Assert.Equal("false", result.Aggregations["discontinued"].Buckets[0].Key);
                Assert.Equal(new AggregationStats(42, 2.5, 263.5, 28.8, 1209.6), result.Aggregations["prices"].Stats);
            }

            // istek
            JsonNode body = server.Requests.Single().Json!;
            Assert.Equal(20, body["from"]!.GetValue<int>());
            Assert.Equal(10, body["size"]!.GetValue<int>());

            JsonNode multiMatch = body["query"]!["bool"]!["must"]![0]!["multi_match"]!;
            Assert.Equal("çay", multiMatch["query"]!.GetValue<string>());
            Assert.Equal("bool_prefix", multiMatch["type"]!.GetValue<string>());
            Assert.Equal("and", multiMatch["operator"]!.GetValue<string>());
            Assert.Equal("AUTO", multiMatch["fuzziness"]!.GetValue<string>());
            Assert.Equal("name^3", multiMatch["fields"]![0]!.GetValue<string>());

            JsonArray filter = body["query"]!["bool"]!["filter"]!.AsArray();
            Assert.Equal("t1", filter[0]!["term"]!["canTenant"]!.GetValue<string>()); // tenant her zaman filtre
            Assert.Equal(10, filter[1]!["range"]!["price"]!["gte"]!.GetValue<int>());
            Assert.Equal(50, filter[1]!["range"]!["price"]!["lte"]!.GetValue<int>());
            Assert.True(body["query"]!["bool"]!["must_not"]![0]!["term"]!["discontinued"]!.GetValue<bool>());

            Assert.Equal("desc", body["sort"]![0]!["price"]!["order"]!.GetValue<string>());
            Assert.NotNull(body["highlight"]!["fields"]!["name"]);
            Assert.Equal("category", body["aggs"]!["categories"]!["terms"]!["field"]!.GetValue<string>());
            Assert.Equal(5, body["aggs"]!["categories"]!["terms"]!["size"]!.GetValue<int>());
            Assert.Equal("price", body["aggs"]!["prices"]!["stats"]!["field"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Host_scope_excludes_tenant_documents_and_empty_text_matches_all()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build();
        using (provider)
        {
            server.Respond(HttpMethod.Post, "/app-products/_search", HttpStatusCode.OK, """{"hits":{"total":{"value":0},"hits":[]}}""");

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, null);
            using (scope)
                await index.SearchAsync(new SearchQuery(), TestContext.Current.CancellationToken);

            JsonNode query = server.Requests.Single().Json!["query"]!["bool"]!;
            Assert.Null(query["must"]);
            Assert.Equal("canTenant", query["must_not"]![0]!["exists"]!["field"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Get_and_delete_use_scoped_ids()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build();
        using (provider)
        {
            server.Respond(HttpMethod.Get, "/app-products/_doc/t1%3A1", HttpStatusCode.OK,
                """{"_id":"t1:1","found":true,"_source":{"id":"1","name":"Chai Çayı","category":"İçecek","price":18,"discontinued":false,"canTenant":"t1"}}""");
            server.Respond(HttpMethod.Get, "/app-products/_doc/t1%3A9", HttpStatusCode.NotFound, """{"_id":"t1:9","found":false}""");
            server.Respond(HttpMethod.Delete, "/app-products/_doc/t1%3A1?refresh=wait_for", HttpStatusCode.OK, """{"result":"deleted"}""");
            server.Respond(HttpMethod.Delete, "/app-products/_doc/t1%3A9?refresh=wait_for", HttpStatusCode.NotFound, """{"result":"not_found"}""");

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, "t1");
            using (scope)
            {
                CancellationToken ct = TestContext.Current.CancellationToken;
                Assert.Equal("Chai Çayı", (await index.GetAsync("1", ct))!.Name);
                Assert.Null(await index.GetAsync("9", ct));
                Assert.True(await index.DeleteAsync("1", ct));
                Assert.False(await index.DeleteAsync("9", ct));
            }
        }
    }

    [Fact]
    public async Task Errors_become_search_exceptions_and_api_key_is_sent()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build(o => o.ApiKey = "secret-key");
        using (provider)
        {
            server.Respond(HttpMethod.Post, "/app-products/_search", HttpStatusCode.BadRequest, """
                {"error":{"root_cause":[],"type":"search_phase_execution_exception","reason":"all shards failed"},"status":400}
                """);

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, "t1");
            using (scope)
            {
                SearchException ex = await Assert.ThrowsAsync<SearchException>(() => index.SearchAsync(new SearchQuery { Text = "x" }, TestContext.Current.CancellationToken));
                Assert.Equal(400, ex.StatusCode);
                Assert.Equal("search_phase_execution_exception", ex.ErrorType);
                Assert.Contains("all shards failed", ex.Message, StringComparison.Ordinal);
            }

            Assert.Equal("ApiKey secret-key", server.Requests.Single().Authorization);
        }
    }

    [Fact]
    public async Task Fails_over_to_the_next_node_and_keeps_the_dead_node_out_of_rotation()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build(o =>
        {
            o.Nodes = [new Uri("http://es1.test:9200"), new Uri("http://es2.test:9200"), new Uri("http://es3.test:9200")];
            o.DeadTimeout = TimeSpan.FromMinutes(5);
        });
        using (provider)
        {
            server.Respond(HttpMethod.Post, "/app-products/_search", HttpStatusCode.OK, """{"hits":{"total":{"value":0},"hits":[]}}""");
            server.DownHosts.Add("es1.test");
            server.UnavailableHosts.Add("es2.test");

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, "t1");
            using (scope)
            {
                CancellationToken ct = TestContext.Current.CancellationToken;
                for (int i = 0; i < 6; i++)
                    Assert.Equal(0, (await index.SearchAsync(new SearchQuery(), ct)).Total);
            }

            // Her bozuk düğüm en fazla bir kez denendi; sonra yalnızca sağlam düğüm.
            Assert.Equal(1, server.Requests.Count(r => r.Host == "es1.test"));
            Assert.Equal(1, server.Requests.Count(r => r.Host == "es2.test"));
            Assert.Equal(6, server.Requests.Count(r => r.Host == "es3.test"));
        }
    }

    [Fact]
    public async Task When_every_node_fails_the_error_names_them()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build(o => o.Nodes = [new Uri("http://es1.test:9200"), new Uri("http://es2.test:9200")]);
        using (provider)
        {
            server.DownHosts.Add("es1.test");
            server.UnavailableHosts.Add("es2.test");

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, "t1");
            using (scope)
            {
                SearchException ex = await Assert.ThrowsAsync<SearchException>(() => index.SearchAsync(new SearchQuery(), TestContext.Current.CancellationToken));
                Assert.Equal("unavailable", ex.ErrorType);
                Assert.Contains("es1.test", ex.Message, StringComparison.Ordinal);
                Assert.Contains("es2.test", ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Bulk_retries_only_rejected_documents()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build(o => o.BulkRetryDelay = TimeSpan.Zero);
        using (provider)
        {
            server.Respond(HttpMethod.Post, "/_bulk?refresh=wait_for", HttpStatusCode.OK, """
                {"errors":true,"items":[
                  {"index":{"_id":"t1:1","status":201}},
                  {"index":{"_id":"t1:2","status":429,"error":{"type":"es_rejected_execution_exception","reason":"queue full"}}},
                  {"index":{"_id":"t1:3","status":400,"error":{"type":"mapper_parsing_exception","reason":"bad"}}}
                ]}
                """);
            server.Respond(HttpMethod.Post, "/_bulk?refresh=wait_for", HttpStatusCode.OK, """{"errors":false,"items":[{"index":{"_id":"t1:2","status":201}}]}""");

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, "t1");
            using (scope)
            {
                SearchBulkResult result = await index.IndexManyAsync(SearchTestHost.Catalog[..3], TestContext.Current.CancellationToken);

                Assert.Equal(2, result.Succeeded);
                Assert.Equal("3", Assert.Single(result.Failures).Id); // eşleme hatası tekrarlanmaz
            }

            Assert.Equal(2, server.Requests.Count);
            string[] retried = server.Requests[1].Body!.TrimEnd('\n').Split('\n');
            Assert.Equal(2, retried.Length); // yalnızca reddedilen belge
            Assert.Equal("t1:2", JsonNode.Parse(retried[0])!["index"]!["_id"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Compression_gzips_request_bodies()
    {
        (ServiceProvider provider, FakeElasticsearch server) = Build(o => o.EnableCompression = true);
        using (provider)
        {
            server.Respond(HttpMethod.Post, "/app-products/_search", HttpStatusCode.OK, """{"hits":{"total":{"value":0},"hits":[]}}""");

            (IServiceScope scope, ISearchIndex<Product> index) = SearchTestHost.Index(provider, "t1");
            using (scope)
                await index.SearchAsync(new SearchQuery { Text = "çay" }, TestContext.Current.CancellationToken);

            FakeElasticsearch.Request request = server.Requests.Single();
            Assert.Equal("gzip", request.ContentEncoding);
            Assert.Equal("çay", request.Json!["query"]!["bool"]!["must"]![0]!["multi_match"]!["query"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("my-deployment:dXMtZWFzdC0xLmF3cy5mb3VuZC5pbyRjZWM2ZjI2MWE3NGJmMjRjZTMzYmI4ODExYjg0Mjk0ZiRjNmMyY2E2ZDA0MjI0OWFmMGNjN2Q3YTllOTYyNTc0Mw==", "https://cec6f261a74bf24ce33bb8811b84294f.us-east-1.aws.found.io/")]
    [InlineData("name:ZXhhbXBsZS5jbG91ZDo5MjQzJGVzLWlkJGtiLWlk", "https://es-id.example.cloud:9243/")]
    public void Cloud_id_resolves_to_the_elasticsearch_endpoint(string cloudId, string expected) =>
        Assert.Equal(expected, ElasticsearchOptions.ParseCloudId(cloudId).ToString());

    [Fact]
    public void Index_prefix_must_be_lowercase()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddCanSearch().UseElasticsearch(o => o.IndexPrefix = "App-"));
    }

    /// <summary>İstekleri kaydeden, tanımlı yanıtları dönen sahte sunucu.</summary>
    internal sealed class FakeElasticsearch : HttpMessageHandler
    {
        private readonly Dictionary<(string Method, string Path), Queue<(HttpStatusCode Status, string? Body)>> _responses = [];

        public List<Request> Requests { get; } = [];

        /// <summary>Bu adreslere istek bağlantı hatası verir.</summary>
        public HashSet<string> DownHosts { get; } = [];

        /// <summary>Bu adresler 503 döner.</summary>
        public HashSet<string> UnavailableHosts { get; } = [];

        public void ClearResponses() => _responses.Clear();

        /// <summary>Aynı istek için sırayla dönülecek yanıtlar; son yanıt sonraki isteklerde de kullanılır.</summary>
        public void Respond(HttpMethod method, string pathAndQuery, HttpStatusCode status, string? body = null)
        {
            if (!_responses.TryGetValue((method.Method, pathAndQuery), out var queue))
                _responses[(method.Method, pathAndQuery)] = queue = new Queue<(HttpStatusCode, string?)>();
            queue.Enqueue((status, body));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.PathAndQuery;
            string host = request.RequestUri.Host;
            string? body = null;
            if (request.Content is not null)
            {
                byte[] bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                    using var plain = new MemoryStream();
                    await gzip.CopyToAsync(plain, cancellationToken);
                    bytes = plain.ToArray();
                }

                body = Encoding.UTF8.GetString(bytes);
            }

            Requests.Add(new Request(
                request.Method,
                path,
                body,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization?.ToString(),
                host,
                request.Content?.Headers.ContentEncoding.FirstOrDefault()));

            if (DownHosts.Contains(host))
                throw new HttpRequestException($"Connection refused ({host})");
            if (UnavailableHosts.Contains(host))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            if (!_responses.TryGetValue((request.Method.Method, path), out var queue))
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent($"beklenmeyen istek: {request.Method} {path}") };

            (HttpStatusCode Status, string? Body) response = queue.Count > 1 ? queue.Dequeue() : queue.Peek();

            var message = new HttpResponseMessage(response.Status);
            if (response.Body is not null)
                message.Content = new StringContent(response.Body, Encoding.UTF8, "application/json");
            return message;
        }

        internal sealed record Request(HttpMethod Method, string PathAndQuery, string? Body, string? ContentType, string? Authorization, string Host, string? ContentEncoding)
        {
            public JsonNode? Json => Body is null ? null : JsonNode.Parse(Body);
        }
    }
}
