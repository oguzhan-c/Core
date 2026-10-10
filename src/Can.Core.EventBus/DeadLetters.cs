using System.Globalization;

namespace Can.Core.EventBus;

/// <summary>
/// Tüketicinin DLQ'su (RabbitMQ <c>{kuyruk}.dlq</c>, Kafka <c>{tüketici}.dlq</c>, Service Bus subscription'ın
/// dead-letter kuyruğu). Hatanın nedeni giderildikten sonra mesajlar yeniden işlemeye gönderilir: deneme hakları
/// baştan başlar ve mesaj yalnızca bu tüketiciye gider (diğer servisler tekrar almaz).
/// </summary>
/// <example>
/// <code>
/// // ödeme servisi düzeldi: yalnızca o event'leri yeniden dene
/// DeadLetterReplayResult result = await deadLetters.ReplayAsync(new DeadLetterReplayOptions
/// {
///     Filter = m =&gt; m.EventName == "billing.payment-failed",
/// });
/// </code>
/// </example>
public interface IDeadLetterQueue
{
    /// <summary>Tüketici adı (DLQ'nun sahibi).</summary>
    string Consumer { get; }

    /// <summary>DLQ'da bekleyen mesaj sayısı (sağlık kontrolü kullanır); broker bilgi vermiyorsa <see langword="null"/>.</summary>
    Task<long?> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// DLQ'daki mesajları sırayla okur; filtreye uyanları yeniden işlemeye gönderir, uymayanları DLQ'da bırakır.
    /// </summary>
    Task<DeadLetterReplayResult> ReplayAsync(DeadLetterReplayOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Yeniden gönderim ayarları.</summary>
public sealed class DeadLetterReplayOptions
{
    /// <summary>En fazla kaç mesaj yeniden gönderilsin (bir kerede kontrollü miktar; hata sürüyorsa DLQ'ya geri dönerler).</summary>
    public int MaxMessages { get; set; } = 100;

    /// <summary>En fazla kaç mesaj incelensin (filtreye uymayanlar dahil; büyük DLQ'larda işlemi sınırlar).</summary>
    public int MaxScanned { get; set; } = 1000;

    /// <summary>Hangi mesajlar gönderilsin; boşsa hepsi.</summary>
    public Func<DeadLetterMessage, bool>? Filter { get; set; }
}

/// <param name="Replayed">Yeniden işlemeye gönderilen.</param>
/// <param name="Skipped">Filtreye uymadığı için DLQ'da bırakılan.</param>
public sealed record DeadLetterReplayResult(int Replayed, int Skipped);

/// <summary>DLQ'daki bir mesajın özeti (filtre için).</summary>
/// <param name="Headers">Mesajın Can başlıkları (<see cref="EventHeaders"/>).</param>
public sealed record DeadLetterMessage(IReadOnlyDictionary<string, string> Headers)
{
    public Guid? EventId => Guid.TryParse(Headers.GetValueOrDefault(EventHeaders.EventId), out Guid id) ? id : null;

    public string? EventName => Headers.GetValueOrDefault(EventHeaders.EventName);

    public string? TenantId => Headers.GetValueOrDefault(EventHeaders.TenantId);

    /// <summary>Hata mesajı.</summary>
    public string? Error => Headers.GetValueOrDefault(EventHeaders.Error);

    /// <summary>Hatanın tipi (<c>System.TimeoutException</c>) ya da neden (<c>invalid-message</c>).</summary>
    public string? ErrorType => Headers.GetValueOrDefault(EventHeaders.ErrorType);

    public DateTimeOffset? FailedAt =>
        DateTimeOffset.TryParse(Headers.GetValueOrDefault(EventHeaders.FailedAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at) ? at : null;

    /// <summary>Daha önce kaç kez yeniden gönderildi.</summary>
    public int ReplayCount =>
        int.TryParse(Headers.GetValueOrDefault(EventHeaders.ReplayCount), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : 0;
}
