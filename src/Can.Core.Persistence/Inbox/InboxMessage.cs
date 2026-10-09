namespace Can.Core.Persistence.Inbox;

/// <summary>Bir tüketicinin işlediği event (aynı event'in tekrar teslimini ayıklamak için).</summary>
public sealed class InboxMessage
{
    /// <summary>Tüketici adı (kuyruk / consumer group / subscription).</summary>
    public string Consumer { get; set; } = string.Empty;

    public Guid EventId { get; set; }

    public string EventName { get; set; } = string.Empty;

    /// <summary>İşlenme anı (UTC; outbox gibi <c>DateTime</c>: her sağlayıcıda karşılaştırılabilir).</summary>
    public DateTime ProcessedAt { get; set; }
}

public sealed class InboxOptions
{
    /// <summary>İşlenmiş kayıtlar bu süre sonra silinir (broker'ın tekrar teslim edebileceği süreden uzun olmalı).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Temizlik aralığı.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Tenant başına ayrı veritabanı varsa temizlik her tenant'ın veritabanında çalışsın (<c>ITenantStore</c> gerekir).</summary>
    public bool PerTenantDatabases { get; set; }
}
