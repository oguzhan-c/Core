using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CanFx.WebApi.Postman;

/// <summary>Bir OpenAPI belgesinin Postman'deki koleksiyonu.</summary>
internal sealed record PostmanDocumentState(string CollectionUid, string Hash);

/// <summary>Kalıcı durum (bilgisayarda, proje dışında): hangi belge hangi koleksiyona gidiyor.</summary>
internal sealed class PostmanState
{
    public string? WorkspaceId { get; set; }
    public string? EnvironmentUid { get; set; }
    public Dictionary<string, PostmanDocumentState> Documents { get; set; } = new(StringComparer.Ordinal);
}

internal enum PostmanSyncResult { Unchanged, Created, Updated, Recreated }

/// <summary>
/// Koleksiyonu kopya üretmeden günceller: Postman'in "var olan koleksiyonu OpenAPI'den güncelle" uç noktası olmadığı
/// için belge geçici bir koleksiyon olarak içe aktarılır, içeriği kayıtlı koleksiyona yazılır, geçici olan silinir.
/// Böylece koleksiyonun uid'si, paylaşımları ve fork'ları korunur.
/// </summary>
internal static class PostmanSynchronizer
{
    public static async Task<(PostmanDocumentState State, PostmanSyncResult Result)> SyncCollectionAsync(
        PostmanApi api, string? workspaceId, string collectionName, string documentJson, PostmanDocumentState? existing, CancellationToken cancellationToken)
    {
        string hash = Hash(documentJson);
        if (existing is not null && existing.Hash == hash)
            return (existing, PostmanSyncResult.Unchanged);

        JsonObject document = JsonNode.Parse(documentJson)?.AsObject() ?? throw new InvalidOperationException("OpenAPI belgesi okunamadı.");
        if (document["info"] is JsonObject info)
            info["title"] = collectionName;   // koleksiyon adı belge başlığından gelir

        string importedUid = await api.ImportOpenApiAsync(workspaceId, document, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return (new PostmanDocumentState(importedUid, hash), PostmanSyncResult.Created);

        bool replaced = false;
        try
        {
            JsonObject imported = await api.GetCollectionAsync(importedUid, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("İçe aktarılan koleksiyon okunamadı.");
            replaced = await api.ReplaceCollectionAsync(existing.CollectionUid, StripIds(imported, collectionName), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (replaced)
                await api.DeleteCollectionAsync(importedUid, CancellationToken.None).ConfigureAwait(false);
        }

        // Kayıtlı koleksiyon Postman'de silinmişse yeni içe aktarılan onun yerini alır.
        return replaced
            ? (new PostmanDocumentState(existing.CollectionUid, hash), PostmanSyncResult.Updated)
            : (new PostmanDocumentState(importedUid, hash), PostmanSyncResult.Recreated);
    }

    /// <summary>
    /// Ortamı oluşturur ya da günceller. Yalnızca verilen değişkenler değişir; kullanıcının ortama eklediği diğer
    /// değişkenler (token vb.) korunur. Ortamın uid'sini döner.
    /// </summary>
    public static async Task<string> SyncEnvironmentAsync(
        PostmanApi api, string? workspaceId, string name, IReadOnlyDictionary<string, string> variables, string? existingUid, CancellationToken cancellationToken)
    {
        if (existingUid is not null && await api.GetEnvironmentAsync(existingUid, cancellationToken).ConfigureAwait(false) is { } current)
        {
            JsonArray values = current["values"] as JsonArray ?? new JsonArray();
            bool changed = false;
            foreach ((string key, string value) in variables)
            {
                JsonObject? existing = values.OfType<JsonObject>().FirstOrDefault(v => v["key"]?.GetValue<string>() == key);
                if (existing is null)
                {
                    values.Add(Variable(key, value));
                    changed = true;
                }
                else if (existing["value"]?.GetValue<string>() != value)
                {
                    existing["value"] = value;
                    changed = true;
                }
            }

            if (!changed)
                return existingUid;

            var copy = new JsonArray(values.Select(v => v?.DeepClone()).ToArray());
            if (await api.UpdateEnvironmentAsync(existingUid, current["name"]?.GetValue<string>() ?? name, copy, cancellationToken).ConfigureAwait(false))
                return existingUid;
        }

        var initial = new JsonArray(variables.Select(v => (JsonNode)Variable(v.Key, v.Value)).ToArray());
        return await api.CreateEnvironmentAsync(workspaceId, name, initial, cancellationToken).ConfigureAwait(false);
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Başka bir koleksiyonun içeriğini PUT ile yazmadan önce kimlikler temizlenmeli (Postman aksi halde reddeder).
    /// </summary>
    internal static JsonObject StripIds(JsonObject collection, string name)
    {
        var copy = collection.DeepClone().AsObject();
        Strip(copy);
        var info = new JsonObject
        {
            ["name"] = name,
            ["schema"] = collection["info"]?["schema"]?.DeepClone() ?? "https://schema.getpostman.com/json/collection/v2.1.0/collection.json",
        };
        if (collection["info"]?["description"] is { } description)
            info["description"] = description.DeepClone();
        copy["info"] = info;
        return copy;
    }

    private static readonly string[] IdKeys = ["id", "_postman_id", "uid", "_exporter_id"];

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in IdKeys)
                    obj.Remove(key);
                foreach (KeyValuePair<string, JsonNode?> child in obj.ToList())
                    Strip(child.Value);
                break;
            case JsonArray array:
                foreach (JsonNode? child in array)
                    Strip(child);
                break;
        }
    }

    private static JsonObject Variable(string key, string value) => new()
    {
        ["key"] = key,
        ["value"] = value,
        ["type"] = "default",
        ["enabled"] = true,
    };
}
