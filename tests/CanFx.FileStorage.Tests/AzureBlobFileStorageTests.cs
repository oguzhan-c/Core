using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using CanFx.FileStorage.AzureBlob;

namespace CanFx.FileStorage.Tests;

public class AzureSharedKeyTests
{
    private static readonly byte[] Key = Convert.FromBase64String(AzureBlobFileStorageOptions.DevelopmentAccountKey);

    private static string Hmac(string text) => Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Shared_key_string_to_sign_follows_the_documented_layout()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "image/png",
            ["if-none-match"] = "*",
            ["X-MS-Version"] = "2021-12-02",
            ["x-ms-date"] = "Tue, 14 Oct 2026 07:00:00 GMT",
            ["x-ms-blob-type"] = "  BlockBlob ",
        };

        string header = AzureSharedKey.CreateAuthorizationHeader(
            "put",
            "/devstoreaccount1/files/%C3%BCr%C3%BCn.png",
            [new("comp", "list"), new("Prefix", "a/b"), new("include", "metadata"), new("include", "deleted")],
            headers,
            3,
            "devstoreaccount1",
            Key);

        // VERB, Content-Encoding, Content-Language, Content-Length, Content-MD5, Content-Type, Date, If-Modified-Since,
        // If-Match, If-None-Match, If-Unmodified-Since, Range, x-ms-* (küçük harf, sıralı), kaynak + sıralı parametreler
        string expected = "PUT\n\n\n3\n\nimage/png\n\n\n\n*\n\n\n"
            + "x-ms-blob-type:BlockBlob\nx-ms-date:Tue, 14 Oct 2026 07:00:00 GMT\nx-ms-version:2021-12-02\n"
            + "/devstoreaccount1/devstoreaccount1/files/%C3%BCr%C3%BCn.png\ncomp:list\ninclude:deleted,metadata\nprefix:a/b";
        Assert.Equal($"SharedKey devstoreaccount1:{Hmac(expected)}", header);
    }

    [Fact]
    public void Zero_length_is_written_as_empty()
    {
        string header = AzureSharedKey.CreateAuthorizationHeader("PUT", "/a/c", [], new Dictionary<string, string>(), 0, "a", Key);
        Assert.Equal($"SharedKey a:{Hmac("PUT\n\n\n\n\n\n\n\n\n\n\n\n/a/a/c")}", header);
    }

    [Fact]
    public void Read_sas_signs_the_documented_fields()
    {
        var expires = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
        string sas = AzureSharedKey.CreateReadSas("acct", Key, "files", "raporlar/ekim.pdf", expires, "2021-12-02");

        string stringToSign = "r\n\n2026-10-14T08:00:00Z\n/blob/acct/files/raporlar/ekim.pdf\n\n\n\n2021-12-02\nb\n\n\n\n\n\n\n";
        Assert.Equal($"sv=2021-12-02&se=2026-10-14T08%3A00%3A00Z&sr=b&sp=r&sig={Uri.EscapeDataString(Hmac(stringToSign))}", sas);
    }
}

public class AzureBlobFileStorageTests
{
    private sealed class FakeAzure : HttpMessageHandler
    {
        public Dictionary<string, (byte[] Body, string ContentType)> Blobs { get; } = new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Requests { get; } = [];
        public bool ContainerExists { get; set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.Equal("2021-12-02", request.Headers.GetValues("x-ms-version").Single());
            Assert.True(request.Headers.Contains("x-ms-date"));

            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Assert.StartsWith("/devstoreaccount1/files", path);
            string name = path.Length > "/devstoreaccount1/files/".Length ? path["/devstoreaccount1/files/".Length..] : "";
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);

            if (query["restype"] == "container" && request.Method == HttpMethod.Put)
            {
                bool existed = ContainerExists;
                ContainerExists = true;
                return new HttpResponseMessage(existed ? HttpStatusCode.Conflict : HttpStatusCode.Created);
            }

            if (query["comp"] == "list")
            {
                string prefix = query["prefix"] ?? "";
                string blobs = string.Concat(Blobs.Where(b => b.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(b =>
                    $"<Blob><Name>{b.Key}</Name><Properties><Last-Modified>Wed, 14 Oct 2026 07:00:00 GMT</Last-Modified><Content-Length>{b.Value.Body.Length}</Content-Length><Content-Type>{b.Value.ContentType}</Content-Type></Properties></Blob>"));
                string xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults><Blobs>{blobs}</Blobs><NextMarker /></EnumerationResults>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            }

            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("BlockBlob", request.Headers.GetValues("x-ms-blob-type").Single());
                if (request.Headers.TryGetValues("if-none-match", out _) && Blobs.ContainsKey(name))
                {
                    var conflict = new HttpResponseMessage(HttpStatusCode.Conflict);
                    conflict.Headers.Add("x-ms-error-code", "BlobAlreadyExists");
                    return conflict;
                }

                Blobs[name] = (await request.Content!.ReadAsByteArrayAsync(cancellationToken), request.Content.Headers.ContentType!.ToString());
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (!Blobs.TryGetValue(name, out (byte[] Body, string ContentType) blob))
            {
                var missing = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
                missing.Headers.Add("x-ms-error-code", "BlobNotFound");
                return missing;
            }

            if (request.Method == HttpMethod.Delete)
            {
                Blobs.Remove(name);
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : blob.Body);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(blob.ContentType);
            content.Headers.ContentLength = blob.Body.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private static (AzureBlobFileStorage Storage, FakeAzure Fake) Create(Action<AzureBlobFileStorageOptions>? configure = null)
    {
        var fake = new FakeAzure();
        var options = new AzureBlobFileStorageOptions { ConnectionString = "UseDevelopmentStorage=true", Container = "files", KeyPrefix = "app" };
        configure?.Invoke(options);
        return (new AzureBlobFileStorage(new HttpClient(fake), options), fake);
    }

    [Fact]
    public async Task Put_get_head_list_delete_round_trip()
    {
        (AzureBlobFileStorage storage, FakeAzure fake) = Create();
        CancellationToken ct = TestContext.Current.CancellationToken;

        StoredFileInfo saved = await storage.SaveAsync("ürünler/1/fotoğraf.png", new MemoryStream([1, 2, 3]), cancellationToken: ct);
        Assert.Equal(3, saved.Size);
        Assert.Equal("image/png", fake.Blobs["app/ürünler/1/fotoğraf.png"].ContentType);
        Assert.Contains("%C3%BCr%C3%BCnler", fake.Requests[0].RequestUri!.AbsoluteUri);
        Assert.StartsWith("SharedKey devstoreaccount1:", fake.Requests[0].Headers.GetValues("Authorization").Single());

        await using (Stream? stream = await storage.OpenReadAsync("ürünler/1/fotoğraf.png", ct))
        {
            var copy = new MemoryStream();
            await stream!.CopyToAsync(copy, ct);
            Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        }

        Assert.Equal(3, (await storage.GetInfoAsync("ürünler/1/fotoğraf.png", ct))?.Size);
        Assert.Null(await storage.GetInfoAsync("yok.txt", ct));
        Assert.Null(await storage.OpenReadAsync("yok.txt", ct));

        StoredFileInfo listed = Assert.Single(await storage.ListAsync("ürünler", ct).ToArrayAsync(ct));
        Assert.Equal("ürünler/1/fotoğraf.png", listed.Path);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.Zero), listed.LastModified);
        Assert.Single(await storage.ListAsync(cancellationToken: ct).ToArrayAsync(ct)); // önek yok: "app//" olmamalı

        Assert.True(await storage.DeleteAsync("ürünler/1/fotoğraf.png", ct));
        Assert.False(await storage.DeleteAsync("ürünler/1/fotoğraf.png", ct));
    }

    [Fact]
    public async Task Existing_blob_is_protected_with_if_none_match()
    {
        (AzureBlobFileStorage storage, _) = Create();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await storage.SaveAsync("a.txt", new MemoryStream([1]), cancellationToken: ct);

        await Assert.ThrowsAsync<FileAlreadyExistsException>(() => storage.SaveAsync("a.txt", new MemoryStream([2]), cancellationToken: ct));
        await storage.SaveAsync("a.txt", new MemoryStream([3]), new FileSaveOptions { Overwrite = true }, ct);
    }

    [Fact]
    public async Task Container_is_created_once_when_asked()
    {
        (AzureBlobFileStorage storage, FakeAzure fake) = Create(o => o.CreateContainerIfMissing = true);
        fake.ContainerExists = false;
        CancellationToken ct = TestContext.Current.CancellationToken;

        await storage.SaveAsync("a.txt", new MemoryStream([1]), cancellationToken: ct);
        await storage.SaveAsync("b.txt", new MemoryStream([1]), cancellationToken: ct);

        Assert.Single(fake.Requests, r => r.RequestUri!.Query.Contains("restype=container", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Temporary_url_is_a_read_only_sas_for_one_blob()
    {
        (AzureBlobFileStorage storage, _) = Create();

        Uri? url = await storage.GetTemporaryUrlAsync("raporlar/ekim.pdf", TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);

        Assert.NotNull(url);
        Assert.StartsWith("http://127.0.0.1:10000/devstoreaccount1/files/app/raporlar/ekim.pdf?sv=2021-12-02&se=", url.AbsoluteUri);
        Assert.Contains("&sr=b&sp=r&sig=", url.Query);
    }

    [Fact]
    public async Task Entra_id_token_is_sent_as_bearer_and_no_sas_is_issued()
    {
        (AzureBlobFileStorage storage, FakeAzure fake) = Create(o =>
        {
            o.ConnectionString = null;
            o.ServiceUrl = new Uri("http://127.0.0.1:10000/devstoreaccount1");
            o.AccessTokenProvider = _ => ValueTask.FromResult("token-123");
        });

        await storage.SaveAsync("a.txt", new MemoryStream([1]), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Bearer token-123", fake.Requests[0].Headers.Authorization?.ToString());
        Assert.Null(await storage.GetTemporaryUrlAsync("a.txt", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sas_token_is_appended_to_every_request()
    {
        (AzureBlobFileStorage storage, FakeAzure fake) = Create(o =>
        {
            o.ConnectionString = null;
            o.ServiceUrl = new Uri("http://127.0.0.1:10000/devstoreaccount1");
            o.SasToken = "?sv=2021-12-02&sig=abc";
        });

        await storage.ExistsAsync("a.txt", TestContext.Current.CancellationToken);

        Assert.EndsWith("?sv=2021-12-02&sig=abc", fake.Requests[0].RequestUri!.AbsoluteUri);
        Assert.False(fake.Requests[0].Headers.Contains("Authorization"));
    }

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=" + AzureBlobFileStorageOptions.DevelopmentAccountKey + ";EndpointSuffix=core.windows.net", "https://acme.blob.core.windows.net/files/app/a.txt")]
    [InlineData("AccountName=acme;AccountKey=" + AzureBlobFileStorageOptions.DevelopmentAccountKey + ";BlobEndpoint=https://cdn.example.com/", "https://cdn.example.com/files/app/a.txt")]
    [InlineData("UseDevelopmentStorage=true", "http://127.0.0.1:10000/devstoreaccount1/files/app/a.txt")]
    public async Task Connection_strings_resolve_the_endpoint(string connectionString, string expected)
    {
        (AzureBlobFileStorage storage, FakeAzure _) = Create(o => o.ConnectionString = connectionString);
        Uri? url = await storage.GetTemporaryUrlAsync("a.txt", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.StartsWith(expected + "?", url!.AbsoluteUri);
    }

    [Fact]
    public void Missing_credentials_are_reported_at_startup()
    {
        var options = new AzureBlobFileStorageOptions { AccountName = "acme", Container = "files" };
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new AzureBlobFileStorage(new HttpClient(), options));
        Assert.Contains("kimlik doğrulama yok", error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Gerçek Azurite'e karşı (imzanın gerçekten kabul edildiğini doğrular): <c>CAN_AZURITE_URL=http://localhost:10000/devstoreaccount1</c>
/// verilmezse atlanır (<c>./build/ci.sh services</c>).
/// </summary>
public class AzureBlobIntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CAN_AZURITE_URL");

    [Fact]
    public async Task Round_trip_against_azurite()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Url), "CAN_AZURITE_URL verilmedi.");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string container = "can-test-" + Guid.NewGuid().ToString("N")[..12];

        using var http = new HttpClient();
        var storage = new AzureBlobFileStorage(http, new AzureBlobFileStorageOptions
        {
            AccountName = AzureBlobFileStorageOptions.DevelopmentAccountName,
            AccountKey = AzureBlobFileStorageOptions.DevelopmentAccountKey,
            ServiceUrl = new Uri(Url!),
            Container = container,
            KeyPrefix = "app",
            CreateContainerIfMissing = true,
        });

        byte[] data = Encoding.UTF8.GetBytes("merhaba dünya");
        await storage.SaveAsync("ürünler/1/not (kopya).txt", new MemoryStream(data), cancellationToken: ct);
        await Assert.ThrowsAsync<FileAlreadyExistsException>(() => storage.SaveAsync("ürünler/1/not (kopya).txt", new MemoryStream(data), cancellationToken: ct));
        await storage.SaveAsync("ürünler/1/not (kopya).txt", new MemoryStream(data), new FileSaveOptions { Overwrite = true }, ct);
        await storage.SaveAsync("boş.bin", new MemoryStream(), cancellationToken: ct);

        StoredFileInfo? info = await storage.GetInfoAsync("ürünler/1/not (kopya).txt", ct);
        Assert.Equal(data.Length, info?.Size);
        Assert.StartsWith("text/plain", info!.ContentType, StringComparison.Ordinal);

        Assert.Equal(new[] { "ürünler/1/not (kopya).txt" }, await storage.ListAsync("ürünler", ct).Select(f => f.Path).ToArrayAsync(ct));
        Assert.Equal(2, await storage.ListAsync(cancellationToken: ct).CountAsync(ct));

        // Süreli adres: kimlik bilgisi olmadan indirilebilir
        Uri url = (await storage.GetTemporaryUrlAsync("ürünler/1/not (kopya).txt", TimeSpan.FromMinutes(5), ct))!;
        using var anonymous = new HttpClient();
        Assert.Equal(data, await anonymous.GetByteArrayAsync(url, ct));

        Assert.True(await storage.DeleteAsync("ürünler/1/not (kopya).txt", ct));
        Assert.False(await storage.ExistsAsync("ürünler/1/not (kopya).txt", ct));
        Assert.Null(await storage.OpenReadAsync("ürünler/1/not (kopya).txt", ct));
    }
}
