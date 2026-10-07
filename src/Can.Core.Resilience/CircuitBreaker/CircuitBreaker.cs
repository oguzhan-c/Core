namespace Can.Core.Resilience;

public enum CircuitState
{
    /// <summary>Normal: istekler geçer, hatalar sayılır.</summary>
    Closed,

    /// <summary>Hata oranı aşıldı: istekler denenmeden reddedilir.</summary>
    Open,

    /// <summary>Bekleme bitti: tek bir deneme isteği geçer; başarılıysa kapanır, değilse yeniden açılır.</summary>
    HalfOpen,

    /// <summary>Elle açık tutuluyor.</summary>
    Isolated,
}

public readonly record struct OnCircuitOpenedArguments<T>(Outcome<T> Outcome, ResilienceContext Context, TimeSpan BreakDuration, bool IsManual);

public readonly record struct OnCircuitClosedArguments<T>(Outcome<T> Outcome, ResilienceContext Context, bool IsManual);

public readonly record struct OnCircuitHalfOpenedArguments(ResilienceContext Context);

public readonly record struct BreakDurationArguments(double FailureRate, int FailureCount, int HalfOpenAttempts);

/// <summary>
/// Hata oranı belirli bir süre içinde eşiği aşarsa devreyi açar; bir süre hiç istek göndermeden hızlıca reddeder
/// (karşı servisi ve kendi kaynaklarını korur), sonra tek bir deneme isteğiyle yokluyor.
/// </summary>
public class CircuitBreakerOptions<T>
{
    /// <summary>Örnekleme süresindeki hata oranı bu değere ulaşırsa (0–1) devre açılır.</summary>
    public double FailureRatio { get; set; } = 0.1;

    /// <summary>Oran hesaplanmadan önce örnekleme süresinde en az bu kadar istek olmalı (az trafikte yanlış açılmasın).</summary>
    public int MinimumThroughput { get; set; } = 100;

    /// <summary>Hata oranının hesaplandığı kayan pencere.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Devrenin açık kaldığı süre.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Açık kalma süresini kendin hesapla (ör. art arda açılışlarda uzat).</summary>
    public Func<BreakDurationArguments, TimeSpan>? BreakDurationGenerator { get; set; }

    public Func<Outcome<T>, bool> ShouldHandle { get; set; } = DefaultPredicates.HandleExceptions;

    public Func<OnCircuitOpenedArguments<T>, ValueTask>? OnOpened { get; set; }

    public Func<OnCircuitClosedArguments<T>, ValueTask>? OnClosed { get; set; }

    public Func<OnCircuitHalfOpenedArguments, ValueTask>? OnHalfOpened { get; set; }

    /// <summary>Devrenin durumunu okumak için (sağlık kontrolü, panel).</summary>
    public CircuitBreakerStateProvider? StateProvider { get; set; }

    /// <summary>Devreyi elle açıp kapatmak için.</summary>
    public CircuitBreakerManualControl? ManualControl { get; set; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FailureRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FailureRatio, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumThroughput, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(SamplingDuration, TimeSpan.FromMilliseconds(20));
        ArgumentOutOfRangeException.ThrowIfLessThan(BreakDuration, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(ShouldHandle);
    }
}

/// <inheritdoc />
public sealed class CircuitBreakerOptions : CircuitBreakerOptions<object?>;

/// <summary>Bağlı devrenin anlık durumu.</summary>
public sealed class CircuitBreakerStateProvider
{
    internal Func<CircuitState>? Getter { get; set; }

    public CircuitState CircuitState => Getter?.Invoke() ?? CircuitState.Closed;
}

/// <summary>Bağlı devre(ler)i elle izole eder ya da kapatır (ör. bakım sırasında).</summary>
public sealed class CircuitBreakerManualControl
{
    private readonly List<ICircuitController> _controllers = [];
    private bool _isolated;

    internal void Attach(ICircuitController controller)
    {
        lock (_controllers)
        {
            _controllers.Add(controller);
            if (_isolated)
                controller.Isolate();
        }
    }

    /// <summary>Devreyi açar ve elle kapatılana kadar açık tutar.</summary>
    public ValueTask IsolateAsync()
    {
        lock (_controllers)
        {
            _isolated = true;
            foreach (ICircuitController controller in _controllers)
                controller.Isolate();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Devreyi kapatır ve sayaçları sıfırlar.</summary>
    public ValueTask CloseAsync()
    {
        lock (_controllers)
        {
            _isolated = false;
            foreach (ICircuitController controller in _controllers)
                controller.Close();
        }

        return ValueTask.CompletedTask;
    }
}

internal interface ICircuitController
{
    void Isolate();

    void Close();
}

internal sealed class CircuitBreakerStrategy<T> : ResilienceStrategy<T>, ICircuitController
{
    private const int BucketCount = 10;

    private readonly CircuitBreakerOptions<T> _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly (long Start, int Successes, int Failures)[] _buckets = new (long, int, int)[BucketCount];
    private readonly long _bucketTicks;

    private CircuitState _state = CircuitState.Closed;
    private DateTimeOffset _blockedUntil;
    private bool _probeInFlight;
    private int _halfOpenAttempts;
    private Exception? _lastException;

    public CircuitBreakerStrategy(CircuitBreakerOptions<T> options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _bucketTicks = Math.Max(1, options.SamplingDuration.Ticks / BucketCount);

        if (options.StateProvider is not null)
            options.StateProvider.Getter = () => State;

        options.ManualControl?.Attach(this);
    }

    internal CircuitState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    protected internal override async ValueTask<Outcome<T>> ExecuteCoreAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        bool halfOpened;
        lock (_lock)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            halfOpened = false;

            switch (_state)
            {
                case CircuitState.Isolated:
                    return Outcome.FromException<T>(new IsolatedCircuitException());

                case CircuitState.Open when now < _blockedUntil:
                    return Outcome.FromException<T>(Broken(_blockedUntil - now));

                case CircuitState.Open:
                    _state = CircuitState.HalfOpen;
                    _probeInFlight = true;
                    halfOpened = true;
                    break;

                case CircuitState.HalfOpen when _probeInFlight:
                    return Outcome.FromException<T>(Broken(TimeSpan.Zero));

                case CircuitState.HalfOpen:
                    _probeInFlight = true;
                    break;
            }
        }

        if (halfOpened && _options.OnHalfOpened is not null)
            await _options.OnHalfOpened(new OnCircuitHalfOpenedArguments(context)).ConfigureAwait(false);

        Outcome<T> outcome = await InvokeAsync(callback, context).ConfigureAwait(false);
        bool handled = _options.ShouldHandle(outcome);

        Func<ValueTask>? notify = null;
        lock (_lock)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (_state == CircuitState.HalfOpen)
            {
                _probeInFlight = false;

                if (handled)
                {
                    _halfOpenAttempts++;
                    notify = Open(outcome, context, now, failureRate: 1, failureCount: 1);
                }
                else if (outcome.Exception is not OperationCanceledException)
                {
                    notify = CloseCore(outcome, context, isManual: false);
                }
                // iptal: yoklama sayılmaz, devre yarı açık kalır
            }
            else if (_state == CircuitState.Closed)
            {
                ref (long Start, int Successes, int Failures) bucket = ref CurrentBucket(now);
                if (handled)
                    bucket.Failures++;
                else
                    bucket.Successes++;

                if (handled)
                {
                    (int successes, int failures) = Totals(now);
                    int total = successes + failures;
                    double rate = total == 0 ? 0 : (double)failures / total;
                    if (total >= _options.MinimumThroughput && rate >= _options.FailureRatio)
                        notify = Open(outcome, context, now, rate, failures);
                }
            }
        }

        if (notify is not null)
            await notify().ConfigureAwait(false);

        return outcome;
    }

    public void Isolate()
    {
        lock (_lock)
        {
            _state = CircuitState.Isolated;
            _probeInFlight = false;
        }
    }

    public void Close()
    {
        lock (_lock)
            _ = CloseCore(default, null, isManual: true);
    }

    private Func<ValueTask>? Open(Outcome<T> outcome, ResilienceContext context, DateTimeOffset now, double failureRate, int failureCount)
    {
        TimeSpan duration = _options.BreakDurationGenerator?.Invoke(new BreakDurationArguments(failureRate, failureCount, _halfOpenAttempts))
            ?? _options.BreakDuration;

        _state = CircuitState.Open;
        _blockedUntil = now + duration;
        _lastException = outcome.Exception;
        ResetBuckets();

        return _options.OnOpened is { } onOpened ? () => onOpened(new OnCircuitOpenedArguments<T>(outcome, context, duration, IsManual: false)) : null;
    }

    private Func<ValueTask>? CloseCore(Outcome<T> outcome, ResilienceContext? context, bool isManual)
    {
        bool wasClosed = _state == CircuitState.Closed;
        _state = CircuitState.Closed;
        _probeInFlight = false;
        _halfOpenAttempts = 0;
        _lastException = null;
        ResetBuckets();

        if (wasClosed || context is null || _options.OnClosed is not { } onClosed)
            return null;

        return () => onClosed(new OnCircuitClosedArguments<T>(outcome, context, isManual));
    }

    private BrokenCircuitException Broken(TimeSpan retryAfter) =>
        new("Devre açık: istek denenmeden reddedildi.", retryAfter, _lastException);

    private ref (long Start, int Successes, int Failures) CurrentBucket(DateTimeOffset now)
    {
        long slot = now.UtcTicks / _bucketTicks;
        ref (long Start, int Successes, int Failures) bucket = ref _buckets[slot % BucketCount];
        if (bucket.Start != slot)
            bucket = (slot, 0, 0);
        return ref bucket;
    }

    private (int Successes, int Failures) Totals(DateTimeOffset now)
    {
        long current = now.UtcTicks / _bucketTicks;
        int successes = 0;
        int failures = 0;

        foreach ((long start, int s, int f) in _buckets)
        {
            if (current - start < BucketCount)
            {
                successes += s;
                failures += f;
            }
        }

        return (successes, failures);
    }

    private void ResetBuckets() => Array.Clear(_buckets);
}
