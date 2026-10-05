namespace Can.Core.Application;

/// <summary><c>AddCanApplication(...)</c> ayarları.</summary>
public sealed class CanApplicationOptions
{
    /// <summary>Tüm <see cref="ISecuredRequest"/>'lerden rol kontrolü olmadan geçen rol. Boş bırakılırsa kimse bypass etmez.</summary>
    public string? AdminRole { get; set; } = "Admin";

    /// <summary><see cref="ICachableRequest.CacheExpiration"/> verilmediğinde kullanılan süre.</summary>
    public TimeSpan DefaultCacheExpiration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Bu süreden uzun süren istekler uyarı olarak loglanır.</summary>
    public TimeSpan SlowRequestThreshold { get; set; } = TimeSpan.FromMilliseconds(500);
}
