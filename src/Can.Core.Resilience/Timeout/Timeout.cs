namespace Can.Core.Resilience;

public readonly record struct OnTimeoutArguments(ResilienceContext Context, TimeSpan Timeout);

/// <summary>
/// İşe süre sınırı koyar. İyimser (optimistic) çalışır: iş, kendisine verilen <see cref="CancellationToken"/>'a uymalıdır;
/// süre dolunca token iptal edilir ve <see cref="TimeoutRejectedException"/> fırlatılır.
/// </summary>
public sealed class TimeoutOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Süreyi bağlama göre hesapla (ör. işlem tipine göre).</summary>
    public Func<ResilienceContext, TimeSpan>? TimeoutGenerator { get; set; }

    public Func<OnTimeoutArguments, ValueTask>? OnTimeout { get; set; }

    internal void Validate()
    {
        if (Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Timeout, TimeSpan.Zero);
    }
}

internal sealed class TimeoutStrategy<T>(TimeoutOptions options, TimeProvider timeProvider) : ResilienceStrategy<T>
{
    protected internal override async ValueTask<Outcome<T>> ExecuteCoreAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        TimeSpan timeout = options.TimeoutGenerator?.Invoke(context) ?? options.Timeout;
        if (timeout <= TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan)
            return await InvokeAsync(callback, context).ConfigureAwait(false);

        CancellationToken outer = context.CancellationToken;

        // Sistem saatinde CancellationTokenSource havuzdan alınır ve dış token'a kayıtla bağlanır (bellek ayırmadan);
        // sahte saatte (testler) TimeProvider'lı kaynak kullanılır.
        bool pooled = timeProvider == TimeProvider.System;
        CancellationTokenSource source = pooled ? CancellationTokenSourcePool.Rent() : new CancellationTokenSource(timeout, timeProvider);
        if (pooled)
            source.CancelAfter(timeout);

        CancellationTokenRegistration registration = outer.CanBeCanceled
            ? outer.UnsafeRegister(static state => ((CancellationTokenSource)state!).Cancel(), source)
            : default;

        context.CancellationToken = source.Token;
        Outcome<T> outcome;
        bool timedOut;
        try
        {
            outcome = await InvokeAsync(callback, context).ConfigureAwait(false);
        }
        finally
        {
            context.CancellationToken = outer;
            await registration.DisposeAsync().ConfigureAwait(false);
            timedOut = source.IsCancellationRequested && !outer.IsCancellationRequested;
            if (pooled)
                CancellationTokenSourcePool.Return(source);
            else
                source.Dispose();
        }

        if (timedOut && outcome.Exception is OperationCanceledException canceled)
        {
            if (options.OnTimeout is not null)
                await options.OnTimeout(new OnTimeoutArguments(context, timeout)).ConfigureAwait(false);

            return Outcome.FromException<T>(new TimeoutRejectedException(timeout, canceled));
        }

        return outcome;
    }
}
