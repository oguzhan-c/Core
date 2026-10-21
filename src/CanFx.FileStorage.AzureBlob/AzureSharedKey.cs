using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CanFx.FileStorage.AzureBlob;

/// <summary>
/// Azure Storage Shared Key imzası ve servis SAS'ı
/// (https://learn.microsoft.com/rest/api/storageservices/authorize-with-shared-key,
/// https://learn.microsoft.com/rest/api/storageservices/create-service-sas).
/// </summary>
internal static class AzureSharedKey
{
    /// <summary>Blob adını yol parçalarına göre kodlar (<c>/</c> korunur).</summary>
    public static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// <c>Authorization: SharedKey hesap:imza</c> değeri.
    /// </summary>
    /// <param name="method">HTTP yöntemi.</param>
    /// <param name="encodedPath">İsteğin kodlanmış yolu (<c>/hesap/container/blob</c> Azurite'te, <c>/container/blob</c> Azure'da).</param>
    /// <param name="query">Kodlanmamış sorgu parametreleri.</param>
    /// <param name="headers">Standart başlıklar (<c>Content-Type</c>, <c>If-None-Match</c>...) ve <c>x-ms-*</c> başlıkları.</param>
    /// <param name="contentLength">Gövde uzunluğu; gövde yoksa ya da 0 ise boş yazılır.</param>
    /// <param name="accountName">Hesap.</param>
    /// <param name="accountKey">Hesap anahtarı.</param>
    public static string CreateAuthorizationHeader(
        string method,
        string encodedPath,
        IEnumerable<KeyValuePair<string, string>> query,
        IReadOnlyDictionary<string, string> headers,
        long? contentLength,
        string accountName,
        byte[] accountKey)
    {
        string Header(string name) => headers.TryGetValue(name, out string? value) ? value : string.Empty;

        var text = new StringBuilder();
        text.Append(method.ToUpperInvariant()).Append('\n');
        text.Append(Header("content-encoding")).Append('\n');
        text.Append(Header("content-language")).Append('\n');
        text.Append(contentLength is > 0 ? contentLength.Value.ToString(CultureInfo.InvariantCulture) : string.Empty).Append('\n');
        text.Append(Header("content-md5")).Append('\n');
        text.Append(Header("content-type")).Append('\n');
        text.Append('\n'); // Date: x-ms-date kullanıldığı için boş
        text.Append(Header("if-modified-since")).Append('\n');
        text.Append(Header("if-match")).Append('\n');
        text.Append(Header("if-none-match")).Append('\n');
        text.Append(Header("if-unmodified-since")).Append('\n');
        text.Append(Header("range")).Append('\n');

        // CanonicalizedHeaders: x-ms-* küçük harf, sıralı, "ad:değer\n"
        foreach ((string name, string value) in headers
            .Where(h => h.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: h.Value.Trim()))
            .OrderBy(h => h.Name, StringComparer.Ordinal))
        {
            text.Append(name).Append(':').Append(value).Append('\n');
        }

        // CanonicalizedResource: /hesap/yol + "\nparametre:değer" (ad küçük harf, sıralı; değerler kodlanmamış)
        text.Append('/').Append(accountName).Append(encodedPath);
        foreach (IGrouping<string, string> parameter in query
            .GroupBy(p => p.Key.ToLowerInvariant(), p => p.Value)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            text.Append('\n').Append(parameter.Key).Append(':').Append(string.Join(',', parameter.Order(StringComparer.Ordinal)));
        }

        return $"SharedKey {accountName}:{Sign(text.ToString(), accountKey)}";
    }

    /// <summary>
    /// Tek bir blob için yalnızca okuma izinli servis SAS'ı (sorgu metni, <c>?</c> olmadan).
    /// </summary>
    /// <param name="accountName">Hesap.</param>
    /// <param name="accountKey">Hesap anahtarı.</param>
    /// <param name="container">Container.</param>
    /// <param name="blobName">Kodlanmamış blob adı.</param>
    /// <param name="expiresAt">Bitiş.</param>
    /// <param name="apiVersion">İmza sürümü (<c>sv</c>); 2020-12-06 ve sonrası biçimi.</param>
    public static string CreateReadSas(string accountName, byte[] accountKey, string container, string blobName, DateTimeOffset expiresAt, string apiVersion)
    {
        string expiry = expiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        const string permissions = "r";
        const string resource = "b";

        string stringToSign = string.Join('\n',
            permissions,                                   // sp
            string.Empty,                                  // st
            expiry,                                        // se
            $"/blob/{accountName}/{container}/{blobName}", // canonicalizedResource
            string.Empty,                                  // si
            string.Empty,                                  // sip
            string.Empty,                                  // spr
            apiVersion,                                    // sv
            resource,                                      // sr
            string.Empty,                                  // snapshot
            string.Empty,                                  // ses
            string.Empty,                                  // rscc
            string.Empty,                                  // rscd
            string.Empty,                                  // rsce
            string.Empty,                                  // rscl
            string.Empty);                                 // rsct

        string signature = Sign(stringToSign, accountKey);
        return $"sv={Uri.EscapeDataString(apiVersion)}&se={Uri.EscapeDataString(expiry)}&sr={resource}&sp={permissions}&sig={Uri.EscapeDataString(signature)}";
    }

    private static string Sign(string stringToSign, byte[] accountKey) =>
        Convert.ToBase64String(HMACSHA256.HashData(accountKey, Encoding.UTF8.GetBytes(stringToSign)));
}
