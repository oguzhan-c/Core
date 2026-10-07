namespace Can.Core.Search.Elasticsearch;

/// <summary>Yazma işlemlerinden sonra belgelerin aramada ne zaman görüneceği.</summary>
public enum ElasticsearchRefresh
{
    /// <summary>Varsayılan: Elasticsearch'ün yenileme aralığında (~1 sn). En hızlısı; toplu yüklemede kullan.</summary>
    None,

    /// <summary>İstek, belgeler aramada görünene kadar bekler (yaz ve hemen ara: testler, küçük yönetim ekranları).</summary>
    WaitFor,

    /// <summary>Hemen yeniler. Pahalıdır; yalnızca testlerde.</summary>
    Immediate,
}

public sealed class ElasticsearchOptions
{
    /// <summary>Sunucu adresi (önünde yük dengeleyici olan küme ya da tek düğüm).</summary>
    public Uri Url { get; set; } = new("http://localhost:9200");

    /// <summary>
    /// Kümenin düğümleri (yük dengeleyici yoksa). Verilirse <see cref="Url"/> yerine bunlar kullanılır: istekler
    /// sırayla dağıtılır, ulaşılamayan düğüm geçici olarak devre dışı kalır ve istek diğer düğümle tekrarlanır.
    /// </summary>
    public List<Uri> Nodes { get; set; } = [];

    /// <summary>Elastic Cloud kimliği (<c>ad:base64</c>, dağıtım sayfasında "Cloud ID"). Verilirse adres buradan çözülür.</summary>
    public string? CloudId { get; set; }

    /// <summary>Düğüm ilk başarısızlıkta en az bu kadar devre dışı kalır; tekrarladıkça artar.</summary>
    public TimeSpan DeadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan MaxDeadTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Toplu yazmada küme "meşgul" (429) dediği belgelerin kaç kez tekrar deneneceği. Bekleme her denemede ikiye katlanır
    /// (<see cref="BulkRetryDelay"/>). Diğer hatalar (eşleme hatası vb.) tekrarlanmaz, sonuçta döner.
    /// </summary>
    public int BulkRetries { get; set; } = 2;

    public TimeSpan BulkRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>İstek gövdeleri gzip ile sıkıştırılır (büyük toplu yüklemelerde ağ trafiğini azaltır). Yanıtlar her zaman açılır.</summary>
    public bool EnableCompression { get; set; }

    /// <summary>API anahtarı (Base64 "encoded" değeri). Koda/appsettings'e yazma: user-secrets ya da ortam değişkeni.</summary>
    public string? ApiKey { get; set; }

    /// <summary>API anahtarı yoksa temel kimlik doğrulama.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Dizin adlarının öneki: aynı kümeyi paylaşan uygulama/ortamlar karışmasın (<c>northwind-dev-</c>).</summary>
    public string IndexPrefix { get; set; } = string.Empty;

    public ElasticsearchRefresh Refresh { get; set; } = ElasticsearchRefresh.None;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (Url is null || !Url.IsAbsoluteUri)
            throw new InvalidOperationException("Elasticsearch adresi (Url) mutlak bir adres olmalı.");
        if (Nodes.Any(n => n is null || !n.IsAbsoluteUri))
            throw new InvalidOperationException("Elasticsearch düğüm adresleri (Nodes) mutlak adres olmalı.");
        if (BulkRetries < 0)
            throw new InvalidOperationException("BulkRetries negatif olamaz.");
        if (!string.IsNullOrEmpty(IndexPrefix) && IndexPrefix != IndexPrefix.ToLowerInvariant())
            throw new InvalidOperationException("Elasticsearch dizin öneki küçük harf olmalı.");
    }

    internal string IndexName(SearchIndexDefinition index) => IndexPrefix + index.Name;

    /// <summary>Kullanılacak düğümler: Cloud ID &gt; Nodes &gt; Url.</summary>
    internal IReadOnlyList<Uri> ResolveNodes() =>
        !string.IsNullOrWhiteSpace(CloudId) ? [ParseCloudId(CloudId)]
        : Nodes.Count > 0 ? Nodes
        : [Url];

    /// <summary>
    /// <c>ad:base64("alan-adı[:port]$es-kimliği[:port]$kibana-kimliği")</c> → <c>https://es-kimliği.alan-adı[:port]</c>.
    /// </summary>
    internal static Uri ParseCloudId(string cloudId)
    {
        string[] tokens = cloudId.Trim().Split(':', 2);
        if (tokens.Length != 2 || string.IsNullOrWhiteSpace(tokens[1]))
            throw new InvalidOperationException("Cloud ID 'ad:base64' biçiminde olmalı.");

        string decoded;
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(tokens[1]));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Cloud ID'nin base64 kısmı çözülemedi.", ex);
        }

        string[] parts = decoded.Split('$');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new InvalidOperationException("Cloud ID alan adı ve Elasticsearch kimliğini içermeli.");

        (string host, string port) = SplitPort(parts[0].Trim(), "443");
        (string id, string esPort) = SplitPort(parts[1].Trim(), port);
        return new Uri(esPort == "443" ? $"https://{id}.{host}" : $"https://{id}.{host}:{esPort}");

        static (string Value, string Port) SplitPort(string value, string defaultPort)
        {
            int colon = value.LastIndexOf(':');
            return colon > 0 ? (value[..colon], value[(colon + 1)..]) : (value, defaultPort);
        }
    }

    internal string RefreshParameter => Refresh switch
    {
        ElasticsearchRefresh.WaitFor => "wait_for",
        ElasticsearchRefresh.Immediate => "true",
        _ => "false",
    };
}
