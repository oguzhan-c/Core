namespace Can.Core.FileStorage.AzureBlob;

/// <summary>
/// Azure Blob ayarları. Kimlik doğrulama için üç yoldan biri:
/// <list type="bullet">
/// <item><see cref="ConnectionString"/> ya da <see cref="AccountName"/> + <see cref="AccountKey"/> (Shared Key; süreli
/// indirme adresi üretilebilir).</item>
/// <item><see cref="SasToken"/>: depolama hesabının paylaşılan erişim imzası (yalnızca verilen izinler).</item>
/// <item><see cref="AccessTokenProvider"/>: Entra ID token'ı (Managed Identity); anahtar saklanmaz. Kapsam:
/// <c>https://storage.azure.com/.default</c>, rol: "Storage Blob Data Contributor".</item>
/// </list>
/// Anahtarlar, SAS ve bağlantı metni appsettings'e değil user-secrets / Key Vault / ortam değişkenine yazılır.
/// </summary>
public sealed class AzureBlobFileStorageOptions
{
    /// <summary>Azurite (yerel emülatör) hesabının herkese açık, belgelenmiş anahtarı. Yalnızca geliştirme içindir.</summary>
    public const string DevelopmentAccountName = "devstoreaccount1";

    /// <summary>Azurite'in belgelenmiş geliştirme anahtarı (gizli değildir; gerçek hesaplarda çalışmaz).</summary>
    public const string DevelopmentAccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>
    /// Azure portalındaki bağlantı metni (<c>DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...</c>) ya da
    /// Azurite için <c>UseDevelopmentStorage=true</c>. Verilirse hesap, anahtar/SAS ve adres buradan okunur.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Hesap adı.</summary>
    public string? AccountName { get; set; }

    /// <summary>Hesap anahtarı (base64). Shared Key imzası ve süreli adres için.</summary>
    public string? AccountKey { get; set; }

    /// <summary>Paylaşılan erişim imzası (<c>sv=...&amp;sig=...</c>, başındaki <c>?</c> olsa da olur).</summary>
    public string? SasToken { get; set; }

    /// <summary>Entra ID erişim token'ı üretir (ör. <c>Azure.Identity</c>'nin <c>DefaultAzureCredential</c>'ı ile).</summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; set; }

    /// <summary>
    /// Blob servisinin adresi. Boşsa <c>https://{AccountName}.blob.core.windows.net</c>. Azurite:
    /// <c>http://127.0.0.1:10000/devstoreaccount1</c>.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Container (S3'teki bucket) adı: küçük harf, rakam ve tire.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Container içinde tüm blob adlarının önüne eklenen klasör (ör. <c>"myapp"</c>).</summary>
    public string? KeyPrefix { get; set; }

    /// <summary>Container yoksa ilk kullanımda oluşturulur (geliştirme ve testler için; üretimde container'ı önceden aç).</summary>
    public bool CreateContainerIfMissing { get; set; }

    /// <summary>REST API sürümü (<c>x-ms-version</c>). Azurite ve tüm Azure bölgeleri destekler.</summary>
    public string ApiVersion { get; set; } = "2021-12-02";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>Bağlantı metnini çözüp ayarları tamamlar ve doğrular.</summary>
    internal ResolvedAzureBlobOptions Resolve()
    {
        string? account = AccountName;
        string? key = AccountKey;
        string? sas = SasToken;
        Uri? service = ServiceUrl;

        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            Dictionary<string, string> parts = ParseConnectionString(ConnectionString);
            if (parts.TryGetValue("UseDevelopmentStorage", out string? development) && development.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                account ??= DevelopmentAccountName;
                key ??= DevelopmentAccountKey;
                service ??= new Uri("http://127.0.0.1:10000/" + DevelopmentAccountName);
            }
            else
            {
                account ??= parts.GetValueOrDefault("AccountName");
                key ??= parts.GetValueOrDefault("AccountKey");
                sas ??= parts.GetValueOrDefault("SharedAccessSignature");
                if (service is null && parts.TryGetValue("BlobEndpoint", out string? endpoint))
                    service = new Uri(endpoint);
                if (service is null && account is not null)
                {
                    string protocol = parts.GetValueOrDefault("DefaultEndpointsProtocol") ?? "https";
                    string suffix = parts.GetValueOrDefault("EndpointSuffix") ?? "core.windows.net";
                    service = new Uri($"{protocol}://{account}.blob.{suffix}");
                }
            }
        }

        if (service is null && !string.IsNullOrWhiteSpace(account))
            service = new Uri($"https://{account}.blob.core.windows.net");

        if (service is null)
            throw new InvalidOperationException("Azure Blob: ConnectionString, AccountName ya da ServiceUrl verilmeli.");
        if (string.IsNullOrWhiteSpace(Container))
            throw new InvalidOperationException("AzureBlobFileStorageOptions.Container boş olamaz.");

        bool sharedKey = !string.IsNullOrWhiteSpace(account) && !string.IsNullOrWhiteSpace(key);
        if (!sharedKey && string.IsNullOrWhiteSpace(sas) && AccessTokenProvider is null)
            throw new InvalidOperationException("Azure Blob: kimlik doğrulama yok. AccountKey (ya da bağlantı metni), SasToken veya AccessTokenProvider verilmeli.");

        byte[]? keyBytes = null;
        if (sharedKey)
        {
            try
            {
                keyBytes = Convert.FromBase64String(key!);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("Azure Blob: AccountKey geçerli bir base64 değil.", exception);
            }
        }

        return new ResolvedAzureBlobOptions(
            new Uri(service.AbsoluteUri.TrimEnd('/') + "/"),
            account,
            keyBytes,
            string.IsNullOrWhiteSpace(sas) ? null : sas.TrimStart('?'),
            AccessTokenProvider,
            Container,
            StoragePath.NormalizePrefix(KeyPrefix),
            ApiVersion,
            CreateContainerIfMissing);
    }

    private static Dictionary<string, string> ParseConnectionString(string connectionString)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
                parts[part[..equals]] = part[(equals + 1)..]; // değer '=' içerebilir (base64 anahtar, SAS)
        }

        return parts;
    }
}

internal sealed record ResolvedAzureBlobOptions(
    Uri ServiceUrl,
    string? AccountName,
    byte[]? AccountKey,
    string? SasToken,
    Func<CancellationToken, ValueTask<string>>? AccessTokenProvider,
    string Container,
    string KeyPrefix,
    string ApiVersion,
    bool CreateContainerIfMissing)
{
    public bool UsesSharedKey => AccountName is not null && AccountKey is not null;
}
