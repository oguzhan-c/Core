using System.Threading.RateLimiting;

namespace Can.Core.Resilience;

public readonly record struct OnRateLimiterRejectedArguments(ResilienceContext Context, TimeSpan? RetryAfter);

/// <summary>
/// İşleri bir <see cref="System.Threading.RateLimiting.RateLimiter"/> ile sınırlar. Eşzamanlılık sınırlayıcısı
/// (<see cref="ConcurrencyLimiter"/>) "bulkhead" görevi görür: yavaşlayan bir bağımlılık tüm thread'leri tüketemez.
/// </summary>
public sealed class RateLimiterStrategyOptions
{
    /// <summary>Kullanılacak sınırlayıcı (token bucket, sliding window ...). Boşsa eşzamanlılık sınırlayıcısı oluşturulur.</summary>
    public RateLimiter? RateLimiter { get; set; }

    /// <summary><see cref="RateLimiter"/> boşken: aynı anda en fazla bu kadar iş.</summary>
    public int PermitLimit { get; set; } = 1000;

    /// <summary><see cref="RateLimiter"/> boşken: sıra bekleyebilecek iş sayısı (0 = beklemeden reddet).</summary>
    public int QueueLimit { get; set; }

    public Func<OnRateLimiterRejectedArguments, ValueTask>? OnRejected { get; set; }
}

internal sealed class RateLimiterStrategy<T>(RateLimiter limiter, RateLimiterStrategyOptions options) : ResilienceStrategy<T>
{
    protected internal override async ValueTask<Outcome<T>> ExecuteCoreAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        RateLimitLease lease;
        try
        {
            lease = await limiter.AcquireAsync(1, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            return Outcome.FromException<T>(exception);
        }

        using (lease)
        {
            if (!lease.IsAcquired)
            {
                TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan value) ? value : null;
                if (options.OnRejected is not null)
                    await options.OnRejected(new OnRateLimiterRejectedArguments(context, retryAfter)).ConfigureAwait(false);

                return Outcome.FromException<T>(new RateLimiterRejectedException(retryAfter));
            }

            return await InvokeAsync(callback, context).ConfigureAwait(false);
        }
    }
}
