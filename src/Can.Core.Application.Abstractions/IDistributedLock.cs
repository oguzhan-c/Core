namespace Can.Core.Application;

/// <summary>
/// Bir kaynağı aynı anda yalnızca bir işin kullanmasını sağlar; uygulama birden çok sunucuda çalışıyorsa sunucular
/// arasında da (Redis: <c>Can.Core.Redis.ScaleOut</c>). Varsayılan uygulama tek süreç içindir.
/// </summary>
/// <example>
/// <code>
/// await using IAsyncDisposable? handle = await locks.TryAcquireAsync($"reorder-report:{tenantId}", TimeSpan.FromMinutes(5), cancellationToken: ct);
/// if (handle is null)
///     return; // başka bir sunucu zaten çalıştırıyor
/// ...
/// </code>
/// </example>
public interface IDistributedLock
{
    /// <summary>Kilidi almaya çalışır; alınamazsa <see langword="null"/>. Dönen nesne dispose edilince kilit bırakılır.</summary>
    /// <param name="resource">Kilitlenecek kaynağın adı (tenant'a özgüyse tenant'ı da içermeli).</param>
    /// <param name="expiry">
    /// Kilidin en uzun ömrü: sunucu çökerse kilit bu süre sonunda kendiliğinden düşer. İşin süresinden uzun olmalı.
    /// </param>
    /// <param name="wait">Kilit doluysa en fazla bu kadar beklenir (varsayılan: hiç beklenmez).</param>
    /// <param name="cancellationToken">İptal.</param>
    Task<IAsyncDisposable?> TryAcquireAsync(string resource, TimeSpan expiry, TimeSpan wait = default, CancellationToken cancellationToken = default);
}
