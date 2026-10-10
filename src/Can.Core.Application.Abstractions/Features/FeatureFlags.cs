namespace Can.Core.Application;

/// <summary>
/// Özellik bayrakları: bir özelliği kod dağıtmadan açıp kapatmak, önce belirli tenant/kullanıcılara ya da kullanıcıların
/// bir yüzdesine açmak. Tanımsız özellik kapalıdır (güvenli varsayılan).
/// </summary>
/// <example>
/// <code>
/// if (await features.IsEnabledAsync("new-checkout", ct))
///     return await NewCheckoutAsync(...);
/// </code>
/// </example>
public interface IFeatureFlags
{
    /// <summary>Özellik o anki kullanıcı ve tenant için açık mı.</summary>
    Task<bool> IsEnabledAsync(string feature, CancellationToken cancellationToken = default);

    /// <summary>O anki kullanıcı ve tenant için açık olan özellikler (arayüz menüleri için).</summary>
    Task<IReadOnlyList<string>> GetEnabledAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// İstek yalnızca bu özelliklerin HEPSİ açıksa çalışır; değilse "bulunamadı" (404, <c>feature_disabled</c>): kapalı
/// özelliğin varlığı belli olmaz.
/// </summary>
public interface IFeatureGatedRequest
{
    IReadOnlyCollection<string> RequiredFeatures { get; }
}

/// <summary>
/// Bir özelliğin kuralı. <see cref="Enabled"/> herkes için açar; değilse <see cref="Users"/>, <see cref="Roles"/>,
/// <see cref="Tenants"/> ya da <see cref="RolloutPercentage"/>'den biri tutan için açıktır. <see cref="ExcludedTenants"/>
/// ve zaman aralığı her durumda geçerlidir.
/// </summary>
public sealed class FeatureDefinition
{
    /// <summary>Herkes için açık.</summary>
    public bool Enabled { get; set; }

    /// <summary>Yalnızca bu tenant'lar için açık.</summary>
    public IList<string> Tenants { get; set; } = [];

    /// <summary>Bu tenant'lar için her zaman kapalı (sorunlu müşteride hemen kapatmak için).</summary>
    public IList<string> ExcludedTenants { get; set; } = [];

    public IList<string> Users { get; set; } = [];

    public IList<string> Roles { get; set; } = [];

    /// <summary>
    /// Kullanıcıların (kullanıcı yoksa tenant'ların) bu yüzdesi için açık (0-100). Aynı kullanıcı her zaman aynı sonucu
    /// alır; yüzde artırılınca önceden açık olanlar açık kalır.
    /// </summary>
    public int? RolloutPercentage { get; set; }

    /// <summary>Bu andan önce kapalı.</summary>
    public DateTimeOffset? EnabledFrom { get; set; }

    /// <summary>Bu andan sonra kapalı.</summary>
    public DateTimeOffset? EnabledUntil { get; set; }

    public string? Description { get; set; }
}

/// <summary>Özellik tanımlarının kaynağı (ayarlar, veritabanı, uzak servis ...).</summary>
public interface IFeatureDefinitionProvider
{
    /// <summary>Tüm tanımlar (ad büyük/küçük harfe duyarsız).</summary>
    ValueTask<IReadOnlyDictionary<string, FeatureDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
}
