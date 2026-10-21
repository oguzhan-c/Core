namespace CanFx.Persistence.Outbox;

/// <summary>
/// Outbox işlemcisinin ayarları.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// Bekleyen mesajların kontrol aralığı.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bir turda en fazla kaç mesaj işlenir.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Toplam deneme hakkı. Tükenince mesaj <see cref="OutboxMessageStatus.Dead"/> olur ve elle yeniden kuyruğa
    /// alınana kadar denenmez. Varsayılan ayarlarla (5 sn'den başlayıp ikiye katlanan bekleme) son deneme ilk
    /// hatadan yaklaşık 45 dakika sonradır; kısa broker/ağ kesintileri mesajı öldürmez.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// İlk hatadan sonraki bekleme; her hatada ikiye katlanır (<c>RetryBaseDelay × 2^(deneme-1)</c>).
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bekleme süresinin üst sınırı.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Bekleme süresine eklenen rastgele oynama oranı (0-1). Aynı anda hata alan çok sayıda mesajın aynı anda
    /// yeniden denenip sistemi yeniden boğmasını önler. Varsayılan ±%20.
    /// </summary>
    public double RetryJitter { get; set; } = 0.2;

    /// <summary>
    /// Mesaj işlenirken kilitli kalma süresi. İşlemci çökerse mesaj bu süre sonunda başka bir tur tarafından alınır;
    /// handler'ların en uzun süresinden büyük olmalı.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Yayınlanmış mesajlar bu süre sonra silinir. <see langword="null"/>: hiç silinmez.
    /// </summary>
    public TimeSpan? RetainProcessedMessagesFor { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Ölü mesajlar bu süre sonra silinir. Varsayılan <see langword="null"/>: incelenip yeniden kuyruğa alınabilsin
    /// diye silinmez.
    /// </summary>
    public TimeSpan? RetainDeadMessagesFor { get; set; }

    /// <summary>
    /// <paramref name="attempts"/>. hatadan sonraki bekleme süresini hesaplar.
    /// </summary>
    /// <param name="attempts">Şimdiye kadarki başarısız deneme sayısı (1'den başlar).</param>
    /// <param name="random">0-1 arası rastgele sayı (oynama için).</param>
    /// <returns>Bir sonraki denemeye kadar beklenecek süre.</returns>
    public TimeSpan RetryDelay(int attempts, double random)
    {
        double exponent = Math.Min(Math.Max(attempts - 1, 0), 30);
        double seconds = Math.Min(RetryBaseDelay.TotalSeconds * Math.Pow(2, exponent), MaxRetryDelay.TotalSeconds);
        double jitter = Math.Clamp(RetryJitter, 0, 1);
        seconds *= 1 + (jitter * ((2 * random) - 1));
        return TimeSpan.FromSeconds(Math.Max(0, seconds));
    }
}
