using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CanFx.WebApi.Postman;

/// <summary>Postman API'nin kullanılan uçları (https://api.postman.com). Kimlik: <c>x-api-key</c> başlığı.</summary>
internal sealed class PostmanApi(HttpClient http)
{
    public const string BaseAddress = "https://api.postman.com/";

    /// <summary>OpenAPI belgesini yeni bir koleksiyon olarak içe aktarır; koleksiyonun uid'sini döner.</summary>
    public async Task<string> ImportOpenApiAsync(string? workspaceId, JsonObject document, CancellationToken cancellationToken)
    {
        string url = "import/openapi" + (string.IsNullOrWhiteSpace(workspaceId) ? "" : "?workspace=" + Uri.EscapeDataString(workspaceId));
        var body = new JsonObject
        {
            ["type"] = "json",
            ["input"] = document.DeepClone(),
            ["options"] = new JsonObject { ["folderStrategy"] = "Tags" },
        };

        JsonObject response = await SendAsync(HttpMethod.Post, url, body, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Postman içe aktarma yanıtı boş.");
        return response["collections"]?.AsArray().FirstOrDefault()?["uid"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Postman içe aktarma yanıtında koleksiyon yok: " + response.ToJsonString());
    }

    /// <summary>Koleksiyonu getirir; yoksa (silinmişse) <see langword="null"/>.</summary>
    public async Task<JsonObject?> GetCollectionAsync(string uid, CancellationToken cancellationToken)
    {
        JsonObject? response = await SendAsync(HttpMethod.Get, "collections/" + uid, null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        return response?["collection"]?.AsObject();
    }

    /// <summary>Koleksiyonun içeriğini değiştirir; koleksiyon yoksa <see langword="false"/>.</summary>
    public async Task<bool> ReplaceCollectionAsync(string uid, JsonObject collection, CancellationToken cancellationToken)
    {
        JsonObject? response = await SendAsync(HttpMethod.Put, "collections/" + uid, new JsonObject { ["collection"] = collection }, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        return response is not null;
    }

    public Task DeleteCollectionAsync(string uid, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Delete, "collections/" + uid, null, cancellationToken, allowNotFound: true);

    /// <summary>Ortamı getirir; yoksa <see langword="null"/>.</summary>
    public async Task<JsonObject?> GetEnvironmentAsync(string uid, CancellationToken cancellationToken)
    {
        JsonObject? response = await SendAsync(HttpMethod.Get, "environments/" + uid, null, cancellationToken, allowNotFound: true).ConfigureAwait(false);
        return response?["environment"]?.AsObject();
    }

    public async Task<string> CreateEnvironmentAsync(string? workspaceId, string name, JsonArray values, CancellationToken cancellationToken)
    {
        string url = "environments" + (string.IsNullOrWhiteSpace(workspaceId) ? "" : "?workspace=" + Uri.EscapeDataString(workspaceId));
        var body = new JsonObject { ["environment"] = new JsonObject { ["name"] = name, ["values"] = values } };
        JsonObject response = await SendAsync(HttpMethod.Post, url, body, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Postman ortam yanıtı boş.");
        return response["environment"]?["uid"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Postman ortam yanıtında uid yok: " + response.ToJsonString());
    }

    public async Task<bool> UpdateEnvironmentAsync(string uid, string name, JsonArray values, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["environment"] = new JsonObject { ["name"] = name, ["values"] = values } };
        return await SendAsync(HttpMethod.Put, "environments/" + uid, body, cancellationToken, allowNotFound: true).ConfigureAwait(false) is not null;
    }

    private async Task<JsonObject?> SendAsync(HttpMethod method, string url, JsonObject? body, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return null;

        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string hint = response.StatusCode == HttpStatusCode.Unauthorized ? " (API anahtarı geçersiz ya da süresi dolmuş)" : "";
            throw new HttpRequestException($"Postman {method} {url}: {(int)response.StatusCode}{hint} {Shorten(text)}", null, response.StatusCode);
        }

        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "...";
}
