using System.Net;
using System.Text;
using Can.Core.FileStorage.S3;

namespace Can.Core.FileStorage.Tests;

public class AwsSignatureV4Tests
{
    // AWS belgelerindeki örnek değerler (Signature V4, "GET Object" ve "presigned URL" örnekleri).
    private static readonly S3Credentials Credentials = new("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
    private static readonly DateTime Now = new(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Header_signature_matches_aws_example()
    {
        string authorization = AwsSignatureV4.CreateAuthorizationHeader(
            "GET",
            "/test.txt",
            [],
            [
                new("Host", "examplebucket.s3.amazonaws.com"),
                new("Range", "bytes=0-9"),
                new("x-amz-content-sha256", AwsSignatureV4.EmptyPayloadHash),
                new("x-amz-date", "20130524T000000Z"),
            ],
            AwsSignatureV4.EmptyPayloadHash,
            Now,
            Credentials,
            "us-east-1"
        );

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,"
                + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,"
                + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            authorization
        );
    }

    [Fact]
    public void Presigned_url_matches_aws_example()
    {
        string query = AwsSignatureV4.CreatePresignedQuery("GET", "examplebucket.s3.amazonaws.com", "/test.txt", TimeSpan.FromDays(1), Now, Credentials, "us-east-1");

        Assert.EndsWith("X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", query);
        Assert.Contains("X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request", query);
    }
}

public class S3FileStorageTests
{
    private sealed class FakeS3 : HttpMessageHandler
    {
        public Dictionary<string, (byte[] Body, string ContentType)> Objects { get; } = new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=key/", request.Headers.Authorization?.ToString() ?? request.Headers.GetValues("Authorization").First());

            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Assert.StartsWith("/files/", path);
            string key = path["/files/".Length..];

            if (request.Method == HttpMethod.Put)
            {
                if (request.Headers.TryGetValues("if-none-match", out _) && Objects.ContainsKey(key))
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);

                Objects[key] = (await request.Content!.ReadAsByteArrayAsync(cancellationToken), request.Content.Headers.ContentType!.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Get && key.Length == 0)
            {
                string prefix = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["prefix"] ?? "";
                string contents = string.Concat(
                    Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(o => $"<Contents><Key>{o.Key}</Key><Size>{o.Value.Body.Length}</Size><LastModified>2026-10-06T10:00:00.000Z</LastModified></Contents>")
                );
                string xml = $"<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>false</IsTruncated>{contents}</ListBucketResult>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            }

            if (!Objects.TryGetValue(key, out (byte[] Body, string ContentType) item))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };

            if (request.Method == HttpMethod.Delete)
            {
                Objects.Remove(key);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : item.Body);
            content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(item.ContentType);
            content.Headers.ContentLength = item.Body.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private static (S3FileStorage Storage, FakeS3 Fake) Create()
    {
        var fake = new FakeS3();
        var storage = new S3FileStorage(
            new HttpClient(fake),
            new S3FileStorageOptions
            {
                ServiceUrl = new Uri("http://localhost:9000"),
                Bucket = "files",
                AccessKeyId = "key",
                SecretAccessKey = "secret",
                KeyPrefix = "app",
            }
        );
        return (storage, fake);
    }

    [Fact]
    public async Task Put_get_head_list_delete_round_trip()
    {
        (S3FileStorage storage, FakeS3 fake) = Create();

        StoredFileInfo saved = await storage.SaveAsync("ürünler/1/fotoğraf.png", new MemoryStream([1, 2, 3]));
        Assert.Equal("ürünler/1/fotoğraf.png", saved.Path);
        Assert.Equal(3, saved.Size);
        Assert.True(fake.Objects.ContainsKey("app/ürünler/1/fotoğraf.png"));
        Assert.Equal("image/png", fake.Objects["app/ürünler/1/fotoğraf.png"].ContentType);

        // Türkçe karakterler adreste kodlanır
        Assert.Contains("%C3%BCr%C3%BCnler", fake.Requests[0].RequestUri!.AbsoluteUri);

        await using (Stream? stream = await storage.OpenReadAsync("ürünler/1/fotoğraf.png"))
        {
            var copy = new MemoryStream();
            await stream!.CopyToAsync(copy);
            Assert.Equal(new byte[] { 1, 2, 3 }, copy.ToArray());
        }

        Assert.Equal(3, (await storage.GetInfoAsync("ürünler/1/fotoğraf.png"))?.Size);
        Assert.Null(await storage.GetInfoAsync("yok.txt"));
        Assert.Null(await storage.OpenReadAsync("yok.txt"));

        Assert.Equal(new[] { "ürünler/1/fotoğraf.png" }, await storage.ListAsync("ürünler").Select(f => f.Path).ToArrayAsync());

        Assert.True(await storage.DeleteAsync("ürünler/1/fotoğraf.png"));
        Assert.False(await storage.DeleteAsync("ürünler/1/fotoğraf.png"));
    }

    [Fact]
    public async Task Existing_object_is_protected_with_if_none_match()
    {
        (S3FileStorage storage, _) = Create();
        await storage.SaveAsync("a.txt", new MemoryStream([1]));

        await Assert.ThrowsAsync<FileAlreadyExistsException>(() => storage.SaveAsync("a.txt", new MemoryStream([2])));
        await storage.SaveAsync("a.txt", new MemoryStream([3]), new FileSaveOptions { Overwrite = true });
    }

    [Fact]
    public async Task Temporary_url_is_presigned()
    {
        (S3FileStorage storage, _) = Create();

        Uri? url = await storage.GetTemporaryUrlAsync("raporlar/ekim.pdf", TimeSpan.FromMinutes(10));

        Assert.NotNull(url);
        Assert.StartsWith("http://localhost:9000/files/app/raporlar/ekim.pdf?X-Amz-Algorithm=AWS4-HMAC-SHA256", url.AbsoluteUri);
        Assert.Contains("X-Amz-Expires=600", url.Query);
        Assert.Contains("X-Amz-Signature=", url.Query);
    }
}
