using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Can.Core.FileStorage.S3;

/// <summary>
/// AWS Signature Version 4 (https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-authenticating-requests.html).
/// Hem header ile imzalı istekleri hem de presigned (query string) adresleri üretir.
/// </summary>
internal static class AwsSignatureV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>
    /// <c>Authorization</c> header değeri. <c>headers</c>: imzalanacak header'lar (host ve x-amz-* dahil); adlar
    /// küçük harfe çevrilir.
    /// </summary>
    public static string CreateAuthorizationHeader(
        string method,
        string canonicalUri,
        IEnumerable<KeyValuePair<string, string>> query,
        IEnumerable<KeyValuePair<string, string>> headers,
        string payloadHash,
        DateTime utcNow,
        S3Credentials credentials,
        string region,
        string service = "s3")
    {
        SortedDictionary<string, string> canonicalHeaders = CanonicalHeaders(headers);
        string signedHeaders = string.Join(';', canonicalHeaders.Keys);
        string scope = Scope(utcNow, region, service);

        string signature = Sign(method, canonicalUri, CanonicalQuery(query), canonicalHeaders, signedHeaders, payloadHash, utcNow, scope, credentials, region, service);
        return $"{Algorithm} Credential={credentials.AccessKeyId}/{scope},SignedHeaders={signedHeaders},Signature={signature}";
    }

    /// <summary>Presigned adresin query string'i (<c>?</c> olmadan); yalnızca <c>host</c> imzalanır.</summary>
    public static string CreatePresignedQuery(
        string method,
        string host,
        string canonicalUri,
        TimeSpan expiresIn,
        DateTime utcNow,
        S3Credentials credentials,
        string region,
        string service = "s3")
    {
        int seconds = (int)Math.Clamp(expiresIn.TotalSeconds, 1, 604800); // en fazla 7 gün
        string scope = Scope(utcNow, region, service);

        var query = new List<KeyValuePair<string, string>>
        {
            new("X-Amz-Algorithm", Algorithm),
            new("X-Amz-Credential", $"{credentials.AccessKeyId}/{scope}"),
            new("X-Amz-Date", Timestamp(utcNow)),
            new("X-Amz-Expires", seconds.ToString(CultureInfo.InvariantCulture)),
            new("X-Amz-SignedHeaders", "host"),
        };

        if (credentials.SessionToken is { Length: > 0 } token)
            query.Add(new("X-Amz-Security-Token", token));

        var canonicalHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = host };
        string canonicalQuery = CanonicalQuery(query);
        string signature = Sign(method, canonicalUri, canonicalQuery, canonicalHeaders, "host", UnsignedPayload, utcNow, scope, credentials, region, service);

        return $"{canonicalQuery}&X-Amz-Signature={signature}";
    }

    public static string Timestamp(DateTime utcNow) => utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string HashHex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>RFC 3986: harf, rakam ve <c>-_.~</c> dışındaki her şey %XX.</summary>
    public static string Encode(string value) => Uri.EscapeDataString(value);

    /// <summary>Nesne anahtarı için yol: her parça ayrı kodlanır, <c>/</c> korunur.</summary>
    public static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Encode));

    private static string Sign(
        string method,
        string canonicalUri,
        string canonicalQuery,
        SortedDictionary<string, string> canonicalHeaders,
        string signedHeaders,
        string payloadHash,
        DateTime utcNow,
        string scope,
        S3Credentials credentials,
        string region,
        string service)
    {
        var canonicalRequest = new StringBuilder()
            .Append(method).Append('\n')
            .Append(canonicalUri).Append('\n')
            .Append(canonicalQuery).Append('\n');

        foreach ((string name, string value) in canonicalHeaders)
            canonicalRequest.Append(name).Append(':').Append(value).Append('\n');

        canonicalRequest.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);

        string stringToSign =
            $"{Algorithm}\n{Timestamp(utcNow)}\n{scope}\n{HashHex(Encoding.UTF8.GetBytes(canonicalRequest.ToString()))}";

        byte[] key = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey), utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");

        return Convert.ToHexStringLower(Hmac(key, stringToSign));
    }

    private static string Scope(DateTime utcNow, string region, string service) =>
        $"{utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{region}/{service}/aws4_request";

    private static SortedDictionary<string, string> CanonicalHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string name, string value) in headers)
            result[name.ToLowerInvariant()] = string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return result;
    }

    private static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join(
            '&',
            query
                .Select(p => (Key: Encode(p.Key), Value: Encode(p.Value)))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value}")
        );

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}

/// <summary>Erişim anahtarları. Koda/appsettings'e yazma; secret store kullan.</summary>
public sealed record S3Credentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null);
