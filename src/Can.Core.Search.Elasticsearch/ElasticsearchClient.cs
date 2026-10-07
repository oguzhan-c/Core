using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Can.Core.Search.Elasticsearch;

/// <summary>
/// Elasticsearch REST API'sine ince erişim. <see cref="ISearchIndex{TDocument}"/>'in kapsamadığı işlemler için doğrudan
/// kullanılabilir (ör. özel sorgu DSL'i, alias, reindex): <c>await client.SendAsync(HttpMethod.Post, "/products/_search", body)</c>.
/// </summary>
public sealed class ElasticsearchClient
{
    /// <summary>HttpClient adı: dayanıklılık eklemek için <c>services.AddHttpClient(ElasticsearchClient.HttpClientName).AddCanStandardResilienceHandler()</c>.</summary>
    public const string HttpClientName = "Can.Core.Search.Elasticsearch";

    private const string JsonMediaType = "application/json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ElasticsearchNodePool _nodes;

    public ElasticsearchClient(IHttpClientFactory httpClientFactory, ElasticsearchOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        Options = options;
        _nodes = new ElasticsearchNodePool(options.ResolveNodes(), options.DeadTimeout, options.MaxDeadTimeout, timeProvider ?? TimeProvider.System);
    }

    public ElasticsearchOptions Options { get; }

    /// <summary>Kullanılan düğümler.</summary>
    public IReadOnlyList<Uri> Nodes => _nodes.Nodes.Select(n => n.Uri).ToArray();

    /// <summary>
    /// İstek gönderir. Başarısız yanıtta <see cref="SearchException"/> atar; <paramref name="allowedStatusCodes"/>'taki
    /// kodlar (ör. 404) hata sayılmaz ve gövde döner. Düğüme ulaşılamazsa ya da 502/503/504 dönerse istek diğer
    /// düğümle tekrarlanır.
    /// </summary>
    /// <param name="method">HTTP yöntemi.</param>
    /// <param name="path">Kökten yol ve sorgu (<c>/products/_doc/1?refresh=true</c>).</param>
    /// <param name="body">JSON gövde (yoksa <see langword="null"/>).</param>
    /// <param name="cancellationToken">İptal.</param>
    /// <param name="allowedStatusCodes">Hata sayılmayacak durum kodları.</param>
    public Task<ElasticsearchResponse> SendAsync(
        HttpMethod method,
        string path,
        JsonNode? body = null,
        CancellationToken cancellationToken = default,
        params HttpStatusCode[] allowedStatusCodes)
    {
        byte[]? bytes = body is null ? null : Encoding.UTF8.GetBytes(body.ToJsonString());
        return SendCoreAsync(method, path, bytes, JsonMediaType, cancellationToken, allowedStatusCodes);
    }

    /// <summary>Satır satır JSON (<c>_bulk</c>): her satır bir JSON nesnesi, sonda satır sonu.</summary>
    /// <param name="path">Yol (<c>/_bulk</c>).</param>
    /// <param name="lines">Satırlar.</param>
    /// <param name="cancellationToken">İptal.</param>
    public Task<ElasticsearchResponse> SendNdJsonAsync(string path, IEnumerable<JsonNode> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var builder = new StringBuilder();
        foreach (JsonNode line in lines)
            builder.Append(line.ToJsonString()).Append('\n');

        return SendCoreAsync(HttpMethod.Post, path, Encoding.UTF8.GetBytes(builder.ToString()), "application/x-ndjson", cancellationToken, []);
    }

    private async Task<ElasticsearchResponse> SendCoreAsync(
        HttpMethod method,
        string path,
        byte[]? body,
        string mediaType,
        CancellationToken cancellationToken,
        HttpStatusCode[] allowedStatusCodes)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[]? payload = body is not null && Options.EnableCompression ? Gzip(body) : body;
        HttpClient http = _httpClientFactory.CreateClient(HttpClientName);
        var failures = new List<string>();
        Exception? lastException = null;

        foreach (ElasticsearchNode node in _nodes.CreateView())
        {
            using HttpRequestMessage request = CreateRequest(method, new Uri(node.Uri, path), payload, mediaType);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // bağlantı hatası ya da zaman aşımı: düğüm devre dışı, sıradaki denenir
                _nodes.MarkDead(node);
                failures.Add($"{node.Uri}: {ex.Message}");
                lastException = ex;
                continue;
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    _nodes.MarkDead(node);
                    failures.Add($"{node.Uri}: {(int)response.StatusCode}");
                    continue;
                }

                ElasticsearchNodePool.MarkAlive(node);
                return await ReadAsync(response, allowedStatusCodes, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new SearchException($"Elasticsearch'e ulaşılamadı ({string.Join("; ", failures)}).", lastException!)
        {
            ErrorType = "unavailable",
        };
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, byte[]? payload, string mediaType)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));

        if (payload is not null)
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
            if (Options.EnableCompression)
                content.Headers.ContentEncoding.Add("gzip");
            request.Content = content;
        }

        if (!string.IsNullOrWhiteSpace(Options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", Options.ApiKey);
        else if (!string.IsNullOrWhiteSpace(Options.Username))
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Options.Username}:{Options.Password}"))
            );

        return request;
    }

    private static async Task<ElasticsearchResponse> ReadAsync(HttpResponseMessage response, HttpStatusCode[] allowedStatusCodes, CancellationToken cancellationToken)
    {
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? json = text.Length == 0 ? null : TryParse(text);

        if (response.IsSuccessStatusCode || allowedStatusCodes.Contains(response.StatusCode))
            return new ElasticsearchResponse(response.StatusCode, json);

        JsonNode? error = json is JsonObject ? json["error"] : null;
        string? type = error is JsonObject ? error["type"]?.GetValue<string>() : null;
        string reason = (error is JsonObject ? error["reason"]?.GetValue<string>() : error?.ToJsonString()) ?? text;
        throw new SearchException($"Elasticsearch {(int)response.StatusCode} ({type ?? "hata"}): {reason}")
        {
            StatusCode = (int)response.StatusCode,
            ErrorType = type,
        };
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(data);
        return output.ToArray();
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }
}

/// <summary>Yanıt: durum kodu ve JSON gövde.</summary>
public sealed record ElasticsearchResponse(HttpStatusCode StatusCode, JsonNode? Body);
