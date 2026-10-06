using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Can.Core.FileStorage.S3;

public sealed class S3FileStorageOptions
{
    /// <summary>
    /// Servis adresi: AWS <c>https://s3.eu-central-1.amazonaws.com</c>, MinIO <c>http://localhost:9000</c>,
    /// Cloudflare R2 <c>https://&lt;hesap&gt;.r2.cloudflarestorage.com</c>.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Bölge (AWS: <c>eu-central-1</c>; R2: <c>auto</c>; MinIO: genelde <c>us-east-1</c>).</summary>
    public string Region { get; set; } = "us-east-1";

    public string Bucket { get; set; } = string.Empty;

    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Geçici kimlik bilgileri için (STS).</summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// <c>true</c>: <c>https://servis/bucket/anahtar</c> (MinIO ve R2 için gerekli); <c>false</c>:
    /// <c>https://bucket.servis/anahtar</c> (AWS'nin önerdiği).
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Bucket içinde tüm anahtarların önüne eklenen klasör (ör. <c>"myapp"</c>).</summary>
    public string? KeyPrefix { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    internal void Validate()
    {
        if (ServiceUrl is null)
            throw new InvalidOperationException("S3FileStorageOptions.ServiceUrl boş olamaz.");
        if (string.IsNullOrWhiteSpace(Bucket))
            throw new InvalidOperationException("S3FileStorageOptions.Bucket boş olamaz.");
        if (string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey))
            throw new InvalidOperationException("S3 erişim anahtarları (AccessKeyId, SecretAccessKey) boş olamaz.");
    }
}

/// <summary>S3 isteği başarısız olduğunda (yetki, bucket yok, kota ...).</summary>
public sealed class S3StorageException(HttpStatusCode statusCode, string responseBody)
    : IOException($"S3 isteği başarısız ({(int)statusCode} {statusCode}): {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string ResponseBody { get; } = responseBody;
}

/// <summary>
/// S3 uyumlu depolama. Gövde imzalanmaz (<c>UNSIGNED-PAYLOAD</c>; bütünlüğü HTTPS sağlar), bu sayede büyük dosyalar
/// belleğe alınmadan akıtılır (uzunluğu bilinmeyen akışlar hariç). <c>Overwrite = false</c> ile yazma
/// <c>If-None-Match: *</c> koşuluyla yapılır (aynı anda iki yazan olsa da biri kazanır).
/// </summary>
public sealed class S3FileStorage : IFileStorage
{
    private static readonly XNamespace S3Xml = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    private readonly HttpClient _http;
    private readonly S3FileStorageOptions _options;
    private readonly S3Credentials _credentials;
    private readonly TimeProvider _timeProvider;
    private readonly string _keyPrefix;

    public S3FileStorage(HttpClient http, S3FileStorageOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _http = http;
        _options = options;
        _credentials = new S3Credentials(options.AccessKeyId, options.SecretAccessKey, options.SessionToken);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _keyPrefix = StoragePath.NormalizePrefix(options.KeyPrefix);
    }

    public async Task<StoredFileInfo> SaveAsync(string path, Stream content, FileSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        path = StoragePath.Normalize(path);
        string contentType = options?.ContentType ?? ContentTypes.FromPath(path);

        // S3 PUT uzunluk ister; uzunluğu bilinmeyen akış belleğe alınır.
        Stream body = content;
        MemoryStream? buffer = null;
        if (!content.CanSeek)
        {
            buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            body = buffer;
        }

        try
        {
            long length = body.Length - body.Position;
            var headers = new Dictionary<string, string> { ["content-type"] = contentType };
            if (options?.Overwrite != true)
                headers["if-none-match"] = "*";

            var streamContent = new StreamContent(body);
            streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            streamContent.Headers.ContentLength = length;

            using HttpResponseMessage response = await SendAsync(HttpMethod.Put, Key(path), [], headers, streamContent, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new FileAlreadyExistsException(path);

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return new StoredFileInfo(path, length, contentType, response.Headers.Date ?? _timeProvider.GetUtcNow());
        }
        finally
        {
            if (buffer is not null)
                await buffer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = StoragePath.Normalize(path);
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, Key(path), [], NoHeaders, null, cancellationToken, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }

        try
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task<StoredFileInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        path = StoragePath.Normalize(path);
        using HttpResponseMessage response = await SendAsync(HttpMethod.Head, Key(path), [], NoHeaders, null, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return new StoredFileInfo(
            path,
            response.Content.Headers.ContentLength ?? 0,
            response.Content.Headers.ContentType?.ToString() ?? ContentTypes.FromPath(path),
            response.Content.Headers.LastModified ?? _timeProvider.GetUtcNow()
        );
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        await GetInfoAsync(path, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        // S3 olmayan nesne için de 204 döner; sonucu bildirebilmek için önce bakılır.
        if (!await ExistsAsync(path, cancellationToken).ConfigureAwait(false))
            return false;

        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, Key(StoragePath.Normalize(path)), [], NoHeaders, null, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async IAsyncEnumerable<StoredFileInfo> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string normalized = StoragePath.NormalizePrefix(prefix);
        string keyPrefix = StoragePath.Combine(_keyPrefix, normalized);
        if (keyPrefix.Length > 0)
            keyPrefix += "/";

        string? continuation = null;
        do
        {
            var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("prefix", keyPrefix) };
            if (continuation is not null)
                query.Add(new("continuation-token", continuation));

            XDocument document;
            using (HttpResponseMessage response = await SendAsync(HttpMethod.Get, key: null, query, NoHeaders, null, cancellationToken).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            }

            XElement root = document.Root!;
            XNamespace ns = root.Name.Namespace == XNamespace.None ? XNamespace.None : S3Xml;

            foreach (XElement item in root.Elements(ns + "Contents"))
            {
                string key = item.Element(ns + "Key")!.Value;
                if (key.EndsWith('/'))
                    continue; // "klasör" işaretleri

                string path = _keyPrefix.Length > 0 && key.StartsWith(_keyPrefix + "/", StringComparison.Ordinal) ? key[(_keyPrefix.Length + 1)..] : key;
                yield return new StoredFileInfo(
                    path,
                    long.Parse(item.Element(ns + "Size")?.Value ?? "0", CultureInfo.InvariantCulture),
                    ContentTypes.FromPath(path),
                    DateTimeOffset.Parse(item.Element(ns + "LastModified")?.Value ?? "1970-01-01T00:00:00Z", CultureInfo.InvariantCulture)
                );
            }

            continuation = string.Equals(root.Element(ns + "IsTruncated")?.Value, "true", StringComparison.OrdinalIgnoreCase)
                ? root.Element(ns + "NextContinuationToken")?.Value
                : null;
        } while (continuation is not null);
    }

    public Task<Uri?> GetTemporaryUrlAsync(string path, TimeSpan expiresIn, CancellationToken cancellationToken = default)
    {
        (Uri url, string host, string canonicalUri) = Address(Key(StoragePath.Normalize(path)));
        string query = AwsSignatureV4.CreatePresignedQuery("GET", host, canonicalUri, expiresIn, _timeProvider.GetUtcNow().UtcDateTime, _credentials, _options.Region);
        return Task.FromResult<Uri?>(new Uri($"{url.AbsoluteUri}?{query}"));
    }

    // ---------------------------------------------------------------- HTTP

    private string Key(string path) => StoragePath.Combine(_keyPrefix, path);

    /// <summary>İstek adresi, imzalanacak host ve kanonik yol.</summary>
    private (Uri Url, string Host, string CanonicalUri) Address(string? key)
    {
        Uri service = _options.ServiceUrl!;
        string encodedKey = key is null ? string.Empty : AwsSignatureV4.EncodePath(key);
        string basePath = service.AbsolutePath.TrimEnd('/');

        if (_options.ForcePathStyle)
        {
            string canonical = $"{basePath}/{AwsSignatureV4.Encode(_options.Bucket)}{(key is null ? string.Empty : "/" + encodedKey)}";
            if (key is null)
                canonical += "/";
            return (new Uri($"{service.Scheme}://{service.Authority}{canonical}"), service.Authority, canonical);
        }

        string host = $"{_options.Bucket}.{service.Authority}";
        string path = $"{basePath}/{encodedKey}";
        return (new Uri($"{service.Scheme}://{host}{path}"), host, path);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string? key,
        IReadOnlyList<KeyValuePair<string, string>> query,
        IReadOnlyDictionary<string, string> extraHeaders,
        HttpContent? content,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        (Uri url, string host, string canonicalUri) = Address(key);
        string queryString = string.Join('&', query.Select(p => $"{AwsSignatureV4.Encode(p.Key)}={AwsSignatureV4.Encode(p.Value)}"));
        var requestUri = new Uri(queryString.Length == 0 ? url.AbsoluteUri : $"{url.AbsoluteUri}?{queryString}");

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        string payloadHash = content is null ? AwsSignatureV4.EmptyPayloadHash : AwsSignatureV4.UnsignedPayload;

        var signed = new Dictionary<string, string>(extraHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = AwsSignatureV4.Timestamp(now),
        };
        if (_credentials.SessionToken is { Length: > 0 } token)
            signed["x-amz-security-token"] = token;

        using var request = new HttpRequestMessage(method, requestUri) { Content = content };
        foreach ((string name, string value) in signed)
        {
            if (name is "host" or "content-type")
                continue; // HttpClient yazar (content-type içerik üzerinde)

            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.TryAddWithoutValidation(
            "Authorization",
            AwsSignatureV4.CreateAuthorizationHeader(method.Method, canonicalUri, query, signed, payloadHash, now, _credentials, _options.Region)
        );

        return await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new S3StorageException(response.StatusCode, body);
    }

    /// <summary>Okuma bitince HTTP yanıtını da kapatan akış.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
