using System.Collections.Concurrent;

namespace Can.Core.Application.Locking;

/// <summary>
/// Tek süreç için <see cref="IDistributedLock"/>: tek sunucuda yeterlidir. Süresi dolan kilit (sahibi bırakmadan)
/// başkasınca alınabilir; geç kalan sahibin bırakması o kilidi etkilemez.
/// </summary>
public sealed class InMemoryDistributedLock : IDistributedLock
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly ConcurrentDictionary<string, Holder> _locks = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryDistributedLock(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string resource, TimeSpan expiry, TimeSpan wait = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiry, TimeSpan.Zero);

        DateTimeOffset deadline = _timeProvider.GetUtcNow() + wait;
        while (true)
        {
            if (TryTake(resource, expiry) is { } handle)
                return handle;

            if (_timeProvider.GetUtcNow() >= deadline)
                return null;

            await Task.Delay(PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private Handle? TryTake(string resource, TimeSpan expiry)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var holder = new Holder(Guid.NewGuid(), now + expiry);

        Holder current = _locks.AddOrUpdate(resource, holder, (_, existing) => existing.ExpiresAt <= now ? holder : existing);
        return current == holder ? new Handle(this, resource, holder) : null;
    }

    private sealed record Holder(Guid Token, DateTimeOffset ExpiresAt);

    private sealed class Handle(InMemoryDistributedLock owner, string resource, Holder holder) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            // Yalnızca hâlâ bizimse sil (süresi dolup başkası aldıysa dokunma).
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner._locks.TryRemove(new KeyValuePair<string, Holder>(resource, holder));
            return ValueTask.CompletedTask;
        }
    }
}
