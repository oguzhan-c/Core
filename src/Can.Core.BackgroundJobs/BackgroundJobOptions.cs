namespace Can.Core.BackgroundJobs;

public sealed class BackgroundJobOptions
{
    /// <summary>Kuyrukta bekleyebilecek en fazla iş. Dolunca <c>EnqueueAsync</c> yer açılana kadar bekler.</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>Kuyruktaki işleri aynı anda çalıştıran işçi sayısı.</summary>
    public int WorkerCount { get; set; } = 1;
}

public sealed class RecurringJobOptions
{
    /// <summary>İki çalıştırma arasındaki süre (bir çalıştırma bitmeden diğeri başlamaz).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Uygulama açılır açılmaz bir kez çalışsın mı? Kapalıysa ilk çalıştırma <see cref="Interval"/> sonra.</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    /// Her aktif tenant için ayrı ayrı (kendi scope'unda, o tenant adına) çalıştır. <c>ITenantStore</c> gerekir.
    /// Kapalıysa iş tenant'sız (host) olarak bir kez çalışır.
    /// </summary>
    public bool PerTenant { get; set; }
}
