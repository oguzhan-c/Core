namespace Can.Core.Resilience;

/// <summary>Bir pipeline çalıştırmasının bağlamı; stratejiler arasında taşınır.</summary>
public sealed class ResilienceContext
{
    public ResilienceContext(CancellationToken cancellationToken = default, string? operationKey = null)
    {
        CancellationToken = cancellationToken;
        OperationKey = operationKey;
    }

    /// <summary>O anki iptal token'ı (timeout stratejisi iç çağrılar için daraltır).</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>Loglarda/olaylarda işlemi tanıtan ad.</summary>
    public string? OperationKey { get; }

    /// <summary>Çağıran kodun stratejilere (ör. fallback'e) ilettiği değerler.</summary>
    public IDictionary<string, object?> Properties { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Retry/hedging'in o anki deneme numarası (0 = ilk deneme).</summary>
    public int AttemptNumber { get; internal set; }
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
