using Can.Core.Application.Locking;

namespace Can.Core.Application.Tests;

public class DistributedLockTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Only_one_holder_at_a_time_and_release_frees_it()
    {
        var locks = new InMemoryDistributedLock();
        CancellationToken ct = TestContext.Current.CancellationToken;

        IAsyncDisposable? first = await locks.TryAcquireAsync("report", TimeSpan.FromMinutes(1), cancellationToken: ct);
        Assert.NotNull(first);
        Assert.Null(await locks.TryAcquireAsync("report", TimeSpan.FromMinutes(1), cancellationToken: ct));
        Assert.NotNull(await locks.TryAcquireAsync("other", TimeSpan.FromMinutes(1), cancellationToken: ct)); // farklı kaynak

        await first.DisposeAsync();
        Assert.NotNull(await locks.TryAcquireAsync("report", TimeSpan.FromMinutes(1), cancellationToken: ct));
    }

    [Fact]
    public async Task Waits_until_the_holder_releases()
    {
        var locks = new InMemoryDistributedLock();
        CancellationToken ct = TestContext.Current.CancellationToken;
        IAsyncDisposable first = (await locks.TryAcquireAsync("job", TimeSpan.FromMinutes(1), cancellationToken: ct))!;

        Task<IAsyncDisposable?> waiting = locks.TryAcquireAsync("job", TimeSpan.FromMinutes(1), wait: TimeSpan.FromSeconds(5), cancellationToken: ct);
        await Task.Delay(100, ct);
        Assert.False(waiting.IsCompleted);

        await first.DisposeAsync();
        Assert.NotNull(await waiting);
    }

    [Fact]
    public async Task Expired_lock_can_be_taken_and_the_late_release_does_not_touch_it()
    {
        var time = new ManualTime();
        var locks = new InMemoryDistributedLock(time);
        CancellationToken ct = TestContext.Current.CancellationToken;

        IAsyncDisposable stale = (await locks.TryAcquireAsync("job", TimeSpan.FromSeconds(30), cancellationToken: ct))!;
        time.Now += TimeSpan.FromSeconds(31); // sahibi çöktü, süre doldu

        IAsyncDisposable fresh = (await locks.TryAcquireAsync("job", TimeSpan.FromSeconds(30), cancellationToken: ct))!;
        await stale.DisposeAsync(); // geç kalan bırakma yenisini silmemeli

        Assert.Null(await locks.TryAcquireAsync("job", TimeSpan.FromSeconds(30), cancellationToken: ct));
        await fresh.DisposeAsync();
        Assert.NotNull(await locks.TryAcquireAsync("job", TimeSpan.FromSeconds(30), cancellationToken: ct));
    }
}
