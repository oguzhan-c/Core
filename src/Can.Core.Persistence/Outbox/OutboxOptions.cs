namespace Can.Core.Persistence.Outbox;

public sealed class OutboxOptions
{
    /// <summary>Bekleyen mesajların kontrol aralığı.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Bir turda en fazla kaç mesaj işlenir.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Bu kadar başarısız denemeden sonra mesaj bırakılır (<c>LastError</c> ile incelenebilir).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Mesaj işlenirken kilitli kalma süresi. İşlemci çökerse mesaj bu süre sonunda başka bir tur tarafından alınır;
    /// handler'ların en uzun süresinden büyük olmalı.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Yayınlanmış mesajlar bu süre sonra silinir. <see langword="null"/>: hiç silinmez.</summary>
    public TimeSpan? RetainProcessedMessagesFor { get; set; } = TimeSpan.FromDays(7);
}
