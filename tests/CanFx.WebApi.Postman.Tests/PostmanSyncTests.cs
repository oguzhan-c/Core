using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CanFx.WebApi.Postman;

namespace CanFx.WebApi.Postman.Tests;

public sealed class PostmanSyncTests
{
    private const string Document = """{"openapi":"3.1.1","info":{"title":"Shop | v1","version":"1.0.0"},"paths":{"/products":{"get":{"tags":["Products"]}}}}""";
    private const string ChangedDocument = """{"openapi":"3.1.1","info":{"title":"Shop | v1","version":"1.0.0"},"paths":{"/products":{"get":{"tags":["Products"]},"post":{"tags":["Products"]}}}}""";

    [Fact]
    public async Task First_sync_imports_a_new_collection_with_the_given_name()
    {
        var postman = new FakePostman();
        var api = new PostmanApi(postman.Client);

        var (state, result) = await PostmanSynchronizer.SyncCollectionAsync(api, "ws-1", "Shop", Document, null, CancellationToken.None);

        Assert.Equal(PostmanSyncResult.Created, result);
        Assert.Equal("owner-new1", state.CollectionUid);
        Assert.Equal(new[] { "POST import/openapi?workspace=ws-1" }, postman.Calls);
        Assert.Equal("Shop", postman.LastImport!["input"]!["info"]!["title"]!.GetValue<string>());
        Assert.Equal("Tags", postman.LastImport["options"]!["folderStrategy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unchanged_document_sends_no_request()
    {
        var postman = new FakePostman();
        var existing = new PostmanDocumentState("owner-kept", PostmanSynchronizer.Hash(Document));

        var (state, result) = await PostmanSynchronizer.SyncCollectionAsync(new PostmanApi(postman.Client), null, "Shop", Document, existing, CancellationToken.None);

        Assert.Equal(PostmanSyncResult.Unchanged, result);
        Assert.Same(existing, state);
        Assert.Empty(postman.Calls);
    }

    [Fact]
    public async Task Changed_document_updates_the_same_collection_and_removes_the_temporary_one()
    {
        var postman = new FakePostman();
        var existing = new PostmanDocumentState("owner-kept", PostmanSynchronizer.Hash(Document));

        var (state, result) = await PostmanSynchronizer.SyncCollectionAsync(new PostmanApi(postman.Client), null, "Shop", ChangedDocument, existing, CancellationToken.None);

        Assert.Equal(PostmanSyncResult.Updated, result);
        Assert.Equal("owner-kept", state.CollectionUid);
        Assert.Equal(PostmanSynchronizer.Hash(ChangedDocument), state.Hash);
        Assert.Equal(
            new[] { "POST import/openapi", "GET collections/owner-new1", "PUT collections/owner-kept", "DELETE collections/owner-new1" },
            postman.Calls);

        JsonObject written = postman.LastPut!["collection"]!.AsObject();
        Assert.Equal("Shop", written["info"]!["name"]!.GetValue<string>());
        string json = written.ToJsonString();
        Assert.DoesNotContain("\"id\":", json);
        Assert.DoesNotContain("\"uid\":", json);
        Assert.DoesNotContain("_postman_id", json);
        Assert.Contains("\"key\":\"id\"", json);   // değerler dokunulmaz, yalnızca kimlik alanları
    }

    [Fact]
    public async Task Collection_deleted_in_postman_is_replaced_by_the_new_import()
    {
        var postman = new FakePostman { MissingCollection = "owner-gone" };
        var existing = new PostmanDocumentState("owner-gone", "old-hash");

        var (state, result) = await PostmanSynchronizer.SyncCollectionAsync(new PostmanApi(postman.Client), null, "Shop", Document, existing, CancellationToken.None);

        Assert.Equal(PostmanSyncResult.Recreated, result);
        Assert.Equal("owner-new1", state.CollectionUid);
        Assert.DoesNotContain(postman.Calls, c => c.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Environment_is_created_then_only_changed_variables_are_written()
    {
        var postman = new FakePostman();
        var api = new PostmanApi(postman.Client);
        var variables = new Dictionary<string, string> { ["baseUrl"] = "http://localhost:5000" };

        string uid = await PostmanSynchronizer.SyncEnvironmentAsync(api, "ws-1", "Shop - Development", variables, null, CancellationToken.None);
        Assert.Equal("owner-env1", uid);

        // Kullanıcı ortama kendi değişkenini ekledi; aynı baseUrl ile tekrar: yazma yok.
        postman.EnvironmentValues.Add(new JsonObject { ["key"] = "token", ["value"] = "abc", ["type"] = "secret", ["enabled"] = true });
        postman.Calls.Clear();
        await PostmanSynchronizer.SyncEnvironmentAsync(api, "ws-1", "Shop - Development", variables, uid, CancellationToken.None);
        Assert.Equal(new[] { "GET environments/owner-env1" }, postman.Calls);

        // baseUrl değişti: yazılır, kullanıcının değişkeni korunur.
        postman.Calls.Clear();
        variables["baseUrl"] = "http://localhost:6000";
        await PostmanSynchronizer.SyncEnvironmentAsync(api, "ws-1", "Shop - Development", variables, uid, CancellationToken.None);
        Assert.Equal(new[] { "GET environments/owner-env1", "PUT environments/owner-env1" }, postman.Calls);
        string written = postman.LastPut!.ToJsonString();
        Assert.Contains("http://localhost:6000", written);
        Assert.Contains("token", written);
    }

    [Fact]
    public async Task Invalid_api_key_is_reported_clearly()
    {
        var postman = new FakePostman { Unauthorized = true };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            PostmanSynchronizer.SyncCollectionAsync(new PostmanApi(postman.Client), null, "Shop", Document, null, CancellationToken.None));

        Assert.Contains("API anahtarı", error.Message);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000", "http://localhost:5000")]
    [InlineData("http://[::]:5000/", "http://localhost:5000")]
    [InlineData("https://+:7001", "https://localhost:7001")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    public void Wildcard_addresses_become_localhost(string address, string expected)
    {
        Assert.Equal(expected, PostmanSyncService.NormalizeAddress(address));
    }

    [Fact]
    public async Task State_round_trips_through_the_file()
    {
        string path = Path.Combine(Path.GetTempPath(), "canfx-postman-" + Guid.NewGuid().ToString("N"), "state.json");
        var state = new PostmanState { WorkspaceId = "ws-1", EnvironmentUid = "owner-env1" };
        state.Documents["v1"] = new PostmanDocumentState("owner-c1", "h1");

        await PostmanSyncService.SaveStateAsync(path, state, CancellationToken.None);
        PostmanState loaded = await PostmanSyncService.LoadStateAsync(path, CancellationToken.None);

        Assert.Equal("ws-1", loaded.WorkspaceId);
        Assert.Equal("owner-env1", loaded.EnvironmentUid);
        Assert.Equal(new PostmanDocumentState("owner-c1", "h1"), loaded.Documents["v1"]);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    /// <summary>Postman API'nin bellek içi taklidi.</summary>
    private sealed class FakePostman : HttpMessageHandler
    {
        public FakePostman() => Client = new HttpClient(this) { BaseAddress = new Uri(PostmanApi.BaseAddress) };

        public HttpClient Client { get; }
        public List<string> Calls { get; } = [];
        public JsonObject? LastImport { get; private set; }
        public JsonObject? LastPut { get; private set; }
        public string? MissingCollection { get; init; }
        public bool Unauthorized { get; init; }
        public JsonArray EnvironmentValues { get; private set; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.PathAndQuery.TrimStart('/');
            Calls.Add($"{request.Method} {path}");
            JsonObject? body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?.AsObject();

            if (Unauthorized)
                return Respond(HttpStatusCode.Unauthorized, """{"error":{"name":"AuthenticationError"}}""");

            if (MissingCollection is not null && path.EndsWith(MissingCollection, StringComparison.Ordinal))
                return Respond(HttpStatusCode.NotFound, "{}");

            if (request.Method == HttpMethod.Post && path.StartsWith("import/openapi", StringComparison.Ordinal))
            {
                LastImport = body;
                return Respond(HttpStatusCode.OK, """{"collections":[{"id":"new1","name":"Shop","uid":"owner-new1"}]}""");
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("collections/", StringComparison.Ordinal))
            {
                return Respond(HttpStatusCode.OK, """
                    {"collection":{"info":{"_postman_id":"new1","name":"Shop","schema":"https://schema.getpostman.com/json/collection/v2.1.0/collection.json","uid":"owner-new1","updatedAt":"2026-01-01"},
                     "item":[{"id":"f1","name":"Products","item":[{"id":"r1","uid":"owner-r1","name":"List","request":{"method":"GET","url":{"raw":"{{baseUrl}}/products","query":[{"key":"id","value":"1"}]}}}]}],
                     "variable":[{"id":"v1","key":"baseUrl","value":"/"}]}}
                    """);
            }

            if (request.Method == HttpMethod.Put)
            {
                LastPut = body;
                if (path.StartsWith("environments/", StringComparison.Ordinal))
                    EnvironmentValues = body!["environment"]!["values"]!.DeepClone().AsArray();
                return Respond(HttpStatusCode.OK, """{"collection":{"id":"x","uid":"x"}}""");
            }

            if (request.Method == HttpMethod.Delete)
                return Respond(HttpStatusCode.OK, "{}");

            if (request.Method == HttpMethod.Post && path.StartsWith("environments", StringComparison.Ordinal))
            {
                EnvironmentValues = body!["environment"]!["values"]!.DeepClone().AsArray();
                return Respond(HttpStatusCode.OK, """{"environment":{"id":"env1","name":"Shop - Development","uid":"owner-env1"}}""");
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("environments/", StringComparison.Ordinal))
            {
                var environment = new JsonObject { ["id"] = "env1", ["name"] = "Shop - Development", ["values"] = EnvironmentValues.DeepClone() };
                return Respond(HttpStatusCode.OK, new JsonObject { ["environment"] = environment }.ToJsonString());
            }

            return Respond(HttpStatusCode.BadRequest, "{}");
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string json)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
