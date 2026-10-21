using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace CanFx.FileStorage.AzureBlob;

/// <summary>Azure Blob isteği başarısız olduğunda (yetki, container yok, kota ...).</summary>
public sealed class AzureBlobStorageException(HttpStatusCode statusCode, string? errorCode, string responseBody)
    : IOException($"Azure Blob isteği başarısız ({(int)statusCode} {statusCode}{(errorCode is null ? "" : ", " + errorCode)}): {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Azure'un hata kodu (<c>x-ms-error-code</c>, ör. <c>ContainerNotFound</c>, <c>AuthorizationFailure</c>).</summary>
    public string? ErrorCode { get; } = errorCode;

    public string ResponseBody { get; } = responseBody;
}

/// <summary>
/// Azure Blob Storage (blok blob). Dosya tek <c>Put Blob</c> isteğiyle yazılır (5000 MiB'a kadar); akış belleğe alınmadan
/// gönderilir (uzunluğu bilinmeyen akışlar hariç). <c>Overwrite = false</c> ile yazma <c>If-None-Match: *</c> koşuluyla
/// yapılır: aynı anda iki yazan olsa da biri kazanır.
/// </summary>
public sealed class AzureBlobFileStorage : IFileStorage
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoQuery = [];

    private readonly HttpClient _http;
    private readonly ResolvedAzureBlobOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _containerGate = new(1, 1);
    private volatile bool _containerReady;

    public AzureBlobFileStorage(HttpClient http, AzureBlobFileStorageOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);

        _http = http;
        _options = options.Resolve();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _containerReady = !_options.CreateContainerIfMissing;
    }

    public async Task<StoredFileInfo> SaveAsync(string path, Stream content, FileSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        path = StoragePath.Normalize(path);
        string contentType = options?.ContentType ?? ContentTypes.FromPath(path);
        await EnsureContainerAsync(cancellationToken).ConfigureAwait(false);

        // Put Blob uzunluk ister; uzunluğu bilinmeyen akış belleğe alınır.
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
            var streamContent = new StreamContent(body);
            streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            streamContent.Headers.ContentLength = length;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = streamContent.Headers.ContentType.ToString(),
                ["x-ms-blob-type"] = "BlockBlob",
            };
            if (options?.Overwrite != true)
                headers["if-none-match"] = "*";

            using HttpResponseMessage response = await SendAsync(HttpMethod.Put, BlobPath(path), NoQuery, headers, streamContent, length, cancellationToken).ConfigureAwait(false);

            // Var olan blob: Azure 409 BlobAlreadyExists, bazı sürümler 412 döner
            if (options?.Overwrite != true && response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                throw new FileAlreadyExistsException(path);

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return new StoredFileInfo(path, length, contentType, response.Content.Headers.LastModified ?? response.Headers.Date ?? _timeProvider.GetUtcNow());
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
        HttpResponseMessage response = await SendAsync(HttpMethod.Get, BlobPath(path), NoQuery, NoHeaders, null, null, cancellationToken, HttpCompletionOption.ResponseHeadersRead)
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
        using HttpResponseMessage response = await SendAsync(HttpMethod.Head, BlobPath(path), NoQuery, NoHeaders, null, null, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return new StoredFileInfo(
            path,
            response.Content.Headers.ContentLength ?? 0,
            response.Content.Headers.ContentType?.ToString() ?? ContentTypes.FromPath(path),
            response.Content.Headers.LastModified ?? _timeProvider.GetUtcNow());
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        await GetInfoAsync(path, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        path = StoragePath.Normalize(path);
        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, BlobPath(path), NoQuery, NoHeaders, null, null, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async IAsyncEnumerable<StoredFileInfo> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string normalized = StoragePath.NormalizePrefix(prefix);
        // "app" + "docs" → "app/docs/"; önek yoksa "app/" (Combine boş yolla "app/" verir, "//" olmasın)
        string blobPrefix = string.Join('/', new[] { _options.KeyPrefix, normalized }.Where(part => part.Length > 0));
        if (blobPrefix.Length > 0)
            blobPrefix += "/";

        string? marker = null;
        do
        {
            var query = new List<KeyValuePair<string, string>> { new("restype", "container"), new("comp", "list") };
            if (blobPrefix.Length > 0)
                query.Add(new("prefix", blobPrefix));
            if (marker is not null)
                query.Add(new("marker", marker));

            XDocument document;
            using (HttpResponseMessage response = await SendAsync(HttpMethod.Get, ContainerPath(), query, NoHeaders, null, null, cancellationToken).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    yield break; // container henüz yok: dosya da yok

                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            }

            XElement root = document.Root!;
            foreach (XElement blob in root.Element("Blobs")?.Elements("Blob") ?? Enumerable.Empty<XElement>())
            {
                string name = blob.Element("Name")!.Value;
                string path = _options.KeyPrefix.Length > 0 && name.StartsWith(_options.KeyPrefix + "/", StringComparison.Ordinal) ? name[(_options.KeyPrefix.Length + 1)..] : name;
                XElement? properties = blob.Element("Properties");
                yield return new StoredFileInfo(
                    path,
                    long.Parse(properties?.Element("Content-Length")?.Value ?? "0", CultureInfo.InvariantCulture),
                    properties?.Element("Content-Type")?.Value is { Length: > 0 } type ? type : ContentTypes.FromPath(path),
                    DateTimeOffset.TryParse(properties?.Element("Last-Modified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset modified)
                        ? modified
                        : DateTimeOffset.UnixEpoch);
            }

            marker = root.Element("NextMarker")?.Value is { Length: > 0 } next ? next : null;
        } while (marker is not null);
    }

    /// <summary>
    /// Yalnızca bu blobu, yalnızca okumak için süreli adres (servis SAS). Hesap anahtarı gerekir; SAS ya da Entra ID
    /// token'ı ile çalışılıyorsa <see langword="null"/> döner (dosyayı bir endpoint üzerinden ver).
    /// </summary>
    public Task<Uri?> GetTemporaryUrlAsync(string path, TimeSpan expiresIn, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero);
        if (!_options.UsesSharedKey)
            return Task.FromResult<Uri?>(null);

        string blobName = StoragePath.Combine(_options.KeyPrefix, StoragePath.Normalize(path));
        string sas = AzureSharedKey.CreateReadSas(
            _options.AccountName!,
            _options.AccountKey!,
            _options.Container,
            blobName,
            _timeProvider.GetUtcNow().Add(expiresIn),
            _options.ApiVersion);
        return Task.FromResult<Uri?>(new Uri(new Uri(_options.ServiceUrl, BlobPath(StoragePath.Normalize(path))).AbsoluteUri + "?" + sas));
    }

    // ---------------------------------------------------------------- HTTP

    /// <summary>Servis adresine göreli, kodlanmış yol.</summary>
    private string ContainerPath() => Uri.EscapeDataString(_options.Container);

    private string BlobPath(string path) => $"{ContainerPath()}/{AzureSharedKey.EncodePath(StoragePath.Combine(_options.KeyPrefix, path))}";

    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerReady)
            return;

        await _containerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_containerReady)
                return;

            using HttpResponseMessage response = await SendAsync(HttpMethod.Put, ContainerPath(), [new("restype", "container")], NoHeaders, null, null, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Conflict) // 409: zaten var
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            _containerReady = true;
        }
        finally
        {
            _containerGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        IReadOnlyList<KeyValuePair<string, string>> query,
        IReadOnlyDictionary<string, string> extraHeaders,
        HttpContent? content,
        long? contentLength,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var url = new Uri(_options.ServiceUrl, relativePath);
        string queryString = string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        if (_options.SasToken is { } sas && !_options.UsesSharedKey && _options.AccessTokenProvider is null)
            queryString = queryString.Length == 0 ? sas : $"{queryString}&{sas}";
        var requestUri = new Uri(queryString.Length == 0 ? url.AbsoluteUri : $"{url.AbsoluteUri}?{queryString}");

        var headers = new Dictionary<string, string>(extraHeaders, StringComparer.OrdinalIgnoreCase)
        {
            ["x-ms-date"] = _timeProvider.GetUtcNow().ToString("R", CultureInfo.InvariantCulture),
            ["x-ms-version"] = _options.ApiVersion,
        };

        using var request = new HttpRequestMessage(method, requestUri) { Content = content };
        foreach ((string name, string value) in headers)
        {
            if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
                continue; // içerik üzerinde

            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (_options.UsesSharedKey)
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                AzureSharedKey.CreateAuthorizationHeader(method.Method, requestUri.AbsolutePath, query, headers, contentLength, _options.AccountName!, _options.AccountKey!));
        }
        else if (_options.AccessTokenProvider is { } tokens)
        {
            string token = await tokens(cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? code = response.Headers.TryGetValues("x-ms-error-code", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
        throw new AzureBlobStorageException(response.StatusCode, code, body);
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
