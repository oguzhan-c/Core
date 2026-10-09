using System.Collections.Concurrent;

namespace Can.Core.EventBus;

/// <summary>
/// Inbox: aynı event'in aynı tüketicide iki kez işlenmesini önler (broker'lar "en az bir kez" teslim eder; bağlantı
/// kopunca ya da ack gecikince mesaj tekrar gelir). Kalıcı uygulama (EF Core) handler'ın veritabanı değişiklikleriyle
/// inbox kaydını AYNI transaction'da yazar: ya ikisi birden olur ya hiçbiri.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Event bu tüketicide daha önce işlenmediyse <paramref name="handle"/>'ı çalıştırıp işlendi olarak kaydeder.
    /// </summary>
    /// <param name="services">Event'in DI scope'u (tenant'ı ayarlanmış).</param>
    /// <param name="consumer">Tüketici adı (servis/kuyruk).</param>
    /// <param name="envelope">Event.</param>
    /// <param name="handle">Handler'ları çalıştırır.</param>
    /// <param name="cancellationToken">İptal.</param>
    /// <returns>Çalıştırıldıysa <see langword="true"/>; daha önce işlenmişse <see langword="false"/>.</returns>
    Task<bool> ExecuteOnceAsync(IServiceProvider services, string consumer, EventEnvelope envelope, Func<CancellationToken, Task> handle, CancellationToken cancellationToken);
}

/// <summary>
/// Bellek içi inbox (tek süreç, yeniden başlayınca unutur). Son <see cref="Capacity"/> event'i hatırlar. Testler ve tek
/// örnekli uygulamalar için; birden çok örnekte ya da kalıcı garanti için EF Core inbox'ını kullan.
/// </summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly ConcurrentDictionary<(string, Guid), byte> _processed = new();
    private readonly ConcurrentQueue<(string, Guid)> _order = new();
    private readonly ConcurrentDictionary<(string, Guid), SemaphoreSlim> _locks = new();

    public int Capacity { get; init; } = 100_000;

    public async Task<bool> ExecuteOnceAsync(IServiceProvider services, string consumer, EventEnvelope envelope, Func<CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(handle);
        (string, Guid) key = (consumer, envelope.EventId);
        if (_processed.ContainsKey(key))
            return false;

        // aynı event'in eşzamanlı iki teslimi: biri işlerken diğeri bekler, sonra "işlendi" görür
        SemaphoreSlim gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processed.ContainsKey(key))
                return false;

            await handle(cancellationToken).ConfigureAwait(false);
            if (_processed.TryAdd(key, 0))
            {
                _order.Enqueue(key);
                while (_order.Count > Capacity && _order.TryDequeue(out (string, Guid) old))
                    _processed.TryRemove(old, out _);
            }

            return true;
        }
        finally
        {
            gate.Release();
            _locks.TryRemove(key, out _);
        }
    }
}
