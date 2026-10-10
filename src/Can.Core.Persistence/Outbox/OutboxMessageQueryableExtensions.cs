using Microsoft.EntityFrameworkCore;

namespace Can.Core.Persistence.Outbox;

/// <summary>
/// Outbox yönetim işlemleri (yönetim paneli, operasyon betikleri).
/// </summary>
public static class OutboxMessageQueryableExtensions
{
    /// <summary>
    /// Sorgunun seçtiği yayınlanmamış mesajları (bekleyen ya da ölü) yeniden kuyruğa alır: durum
    /// <see cref="OutboxMessageStatus.Pending"/> olur, deneme sayacı, hata ve bekleme sıfırlanır; mesaj bir sonraki
    /// turda yayınlanır. Yayınlanmış mesajlara dokunulmaz.
    /// </summary>
    /// <param name="messages">Yeniden kuyruğa alınacak mesajları seçen sorgu (ör. <c>db.Set&lt;OutboxMessage&gt;().Where(m =&gt; m.Id == id)</c>).</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    /// <returns>Yeniden kuyruğa alınan mesaj sayısı.</returns>
    /// <example>
    /// <code>
    /// // tek mesaj
    /// await db.Set&lt;OutboxMessage&gt;().Where(m =&gt; m.Id == id).RequeueAsync(ct);
    /// // hatası giderilmiş bir event tipinin tüm ölü mesajları
    /// await db.Set&lt;OutboxMessage&gt;().Where(m =&gt; m.Status == OutboxMessageStatus.Dead &amp;&amp; m.Type.StartsWith("Sales.OrderShipped")).RequeueAsync(ct);
    /// </code>
    /// </example>
    public static Task<int> RequeueAsync(this IQueryable<OutboxMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages
            .Where(m => m.Status != OutboxMessageStatus.Processed)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.Status, OutboxMessageStatus.Pending)
                    .SetProperty(m => m.Attempts, 0)
                    .SetProperty(m => m.LastError, (string?)null)
                    .SetProperty(m => m.NextAttemptAt, (DateTime?)null)
                    .SetProperty(m => m.DeadAt, (DateTime?)null)
                    .SetProperty(m => m.LockedUntil, (DateTime?)null),
                cancellationToken);
    }
}
