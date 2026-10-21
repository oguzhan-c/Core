namespace CanFx.Testing;

/// <summary>
/// Elle ilerletilen saat. <see cref="Advance"/> ya da <see cref="SetUtcNow"/> ile zaman ilerleyince zamanı gelen
/// zamanlayıcılar (<c>Task.Delay(süre, clock)</c>, <c>PeriodicTimer</c>, <c>CreateTimer</c>) çalışır; gerçek
/// bekleme yoktur.
/// </summary>
/// <example>
/// <code>
/// var clock = new TestClock(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
/// services.AddSingleton&lt;TimeProvider&gt;(clock);
/// clock.Advance(TimeSpan.FromHours(1));
/// </code>
/// </example>
public sealed class TestClock : TimeProvider
{
    /// <summary>Varsayılan başlangıç: 2026-01-01 00:00 UTC (testler tarihe bağlı kalmasın diye sabit).</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private readonly List<Timer> _timers = [];
    private DateTimeOffset _now;
    private long _ticks;

    public TestClock(DateTimeOffset? start = null, TimeZoneInfo? localTimeZone = null)
    {
        _now = (start ?? DefaultStart).ToUniversalTime();
        LocalTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
    }

    public override TimeZoneInfo LocalTimeZone { get; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    /// <summary>Zamanı ilerletilen kadar sayar (<c>Stopwatch</c> yerine <c>GetTimestamp</c>/<c>GetElapsedTime</c> kullananlar için).</summary>
    public override long GetTimestamp()
    {
        lock (_gate)
            return _ticks;
    }

    /// <summary>Saati ilerletir; aradaki zamanlayıcıları sırayla çalıştırır.</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "Saat geri alınamaz.");
        SetUtcNow(GetUtcNow() + delta);
    }

    /// <summary>Saati verilen ana getirir (geri alınamaz).</summary>
    public void SetUtcNow(DateTimeOffset value)
    {
        value = value.ToUniversalTime();
        while (true)
        {
            Timer? due;
            lock (_gate)
            {
                if (value < _now)
                    throw new ArgumentOutOfRangeException(nameof(value), "Saat geri alınamaz.");

                due = _timers.Where(t => t.DueAt <= value).OrderBy(t => t.DueAt).FirstOrDefault();
                DateTimeOffset target = due?.DueAt ?? value;
                if (target > _now)
                {
                    _ticks += (target - _now).Ticks;
                    _now = target;
                }

                if (due is null)
                    return;

                if (due.Period is { } period && period > TimeSpan.Zero)
                    due.DueAt += period;
                else
                    _timers.Remove(due);
            }

            due.Fire(); // kilit dışında: geri çağrı saate yeniden dokunabilir
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Bekleyen zamanlayıcı sayısı (testte "bir şey beklemede mi" denetimi için).</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
                return _timers.Count;
        }
    }

    private sealed class Timer(TestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;
                DueAt = clock._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
                clock._timers.Add(this);
            }

            if (dueTime == TimeSpan.Zero)
                clock.SetUtcNow(clock.GetUtcNow()); // hemen çalışsın
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
                clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
