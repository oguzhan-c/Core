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
        using var timeoutSource = new CancellationTokenSource(timeout, timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(outer, timeoutSource.Token);

        context.CancellationToken = linked.Token;
        Outcome<T> outcome;
        try
        {
            outcome = await InvokeAsync(callback, context).ConfigureAwait(false);
        }
        finally
        {
            context.CancellationToken = outer;
        }

        if (timeoutSource.IsCancellationRequested && !outer.IsCancellationRequested && outcome.Exception is OperationCanceledException canceled)
        {
            if (options.OnTimeout is not null)
                await options.OnTimeout(new OnTimeoutArguments(context, timeout)).ConfigureAwait(false);

            return Outcome.FromException<T>(new TimeoutRejectedException(timeout, canceled));
        }

        return outcome;
    }
}
