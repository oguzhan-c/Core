using System.Collections.Concurrent;

namespace Can.Core.Resilience;

/// <summary>Bir pipeline çalıştırmasının bağlamı; stratejiler arasında taşınır.</summary>
public sealed class ResilienceContext
{
    private Dictionary<string, object?>? _properties;

    public ResilienceContext(CancellationToken cancellationToken = default, string? operationKey = null)
    {
        CancellationToken = cancellationToken;
        OperationKey = operationKey;
    }

    /// <summary>O anki iptal token'ı (timeout stratejisi iç çağrılar için daraltır).</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>Loglarda/olaylarda işlemi tanıtan ad.</summary>
    public string? OperationKey { get; internal set; }

    /// <summary>Çağıran kodun stratejilere (ör. fallback'e) ilettiği değerler (ilk erişimde oluşturulur).</summary>
    public IDictionary<string, object?> Properties => _properties ??= new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Retry/hedging'in o anki deneme numarası (0 = ilk deneme).</summary>
    public int AttemptNumber { get; internal set; }

    /// <summary>Pipeline'ın en içte çalıştıracağı iş (her çağrıda yeni delegate oluşturmamak için bağlamda taşınır).</summary>
    internal object? ExecutionCallback { get; set; }

    /// <summary>Kullanıcının verdiği iş (closure oluşturmadan statik callback'e iletmek için).</summary>
    internal object? State { get; set; }

    internal bool HasProperties => _properties is { Count: > 0 };

    /// <summary>Hedging denemeleri için kopya (ayrı token, aynı iş ve özellikler).</summary>
    internal ResilienceContext CloneForAttempt(CancellationToken cancellationToken, int attemptNumber)
    {
        var clone = new ResilienceContext(cancellationToken, OperationKey)
        {
            AttemptNumber = attemptNumber,
            ExecutionCallback = ExecutionCallback,
            State = State,
        };

        if (HasProperties)
        {
            foreach (KeyValuePair<string, object?> property in _properties!)
                clone.Properties[property.Key] = property.Value;
        }

        return clone;
    }

    internal void Reset()
    {
        CancellationToken = default;
        OperationKey = null;
        AttemptNumber = 0;
        ExecutionCallback = null;
        State = null;
        _properties?.Clear();
    }
}

/// <summary>Pipeline'ın kendi oluşturduğu bağlamları yeniden kullanır (sıcak yolda bellek ayırmamak için).</summary>
internal static class ResilienceContextPool
{
    private const int MaxSize = 256;
    private static readonly ConcurrentQueue<ResilienceContext> Pool = new();
    private static int _count;

    public static ResilienceContext Rent(CancellationToken cancellationToken)
    {
        if (Pool.TryDequeue(out ResilienceContext? context))
        {
            Interlocked.Decrement(ref _count);
            context.CancellationToken = cancellationToken;
            return context;
        }

        return new ResilienceContext(cancellationToken);
    }

    public static void Return(ResilienceContext context)
    {
        context.Reset();
        if (Interlocked.Increment(ref _count) <= MaxSize)
            Pool.Enqueue(context);
        else
            Interlocked.Decrement(ref _count);
    }
}

/// <summary>Zaman aşımı için <see cref="CancellationTokenSource"/> havuzu.</summary>
internal static class CancellationTokenSourcePool
{
    private const int MaxSize = 256;
    private static readonly ConcurrentQueue<CancellationTokenSource> Pool = new();
    private static int _count;

    public static CancellationTokenSource Rent()
    {
        if (Pool.TryDequeue(out CancellationTokenSource? source))
        {
            Interlocked.Decrement(ref _count);
            return source;
        }

        return new CancellationTokenSource();
    }

    public static void Return(CancellationTokenSource source)
    {
        // İptal edilmiş (sıfırlanamayan) kaynak yeniden kullanılamaz.
        if (source.TryReset())
        {
            if (Interlocked.Increment(ref _count) <= MaxSize)
            {
                Pool.Enqueue(source);
                return;
            }

            Interlocked.Decrement(ref _count);
        }

        source.Dispose();
    }
}

/// <summary>Stratejilerin olay bildirimleri için ortak argüman.</summary>
public readonly record struct ResilienceEvent(string Strategy, string Name, ResilienceContext Context);

/// <summary>Bir istek, sınırlayıcı tarafından reddedildi (bulkhead/rate limiter dolu).</summary>
public sealed class RateLimiterRejectedException(TimeSpan? retryAfter = null)
    : ExecutionRejectedException(retryAfter is { } r ? $"İstek sınırı aşıldı; {r.TotalSeconds:0.#} sn sonra tekrar dene." : "İstek sınırı aşıldı.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>İşlem zaman aşımına uğradı (timeout stratejisi iptal etti).</summary>
public sealed class TimeoutRejectedException(TimeSpan timeout, Exception? inner = null)
    : ExecutionRejectedException($"İşlem {timeout.TotalMilliseconds:0} ms içinde tamamlanmadı.", inner)
{
    public TimeSpan Timeout { get; } = timeout;
}

/// <summary>Devre açık: istek hiç denenmeden reddedildi.</summary>
public class BrokenCircuitException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : ExecutionRejectedException(message, inner)
{
    /// <summary>Devrenin yeniden denenebileceği süre (yaklaşık).</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Devre elle kapatıldı (<see cref="CircuitBreakerManualControl.IsolateAsync"/>).</summary>
public sealed class IsolatedCircuitException() : BrokenCircuitException("Devre elle izole edildi.");

/// <summary>Pipeline'ın işlemi çalıştırmadan reddettiği durumların tabanı.</summary>
public abstract class ExecutionRejectedException(string message, Exception? inner = null) : Exception(message, inner);
