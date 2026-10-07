namespace Can.Core.Resilience;

/// <summary>Bir strateji: işi (<c>callback</c>) sarar; tekrar dener, keser, sınırlar ...</summary>
public abstract class ResilienceStrategy<T>
{
    protected internal abstract ValueTask<Outcome<T>> ExecuteCoreAsync(
        Func<ResilienceContext, ValueTask<Outcome<T>>> callback,
        ResilienceContext context);

    /// <summary>İşi çalıştırıp exception'ı sonuca çevirir.</summary>
    protected static async ValueTask<Outcome<T>> InvokeAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        try
        {
            return await callback(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Outcome.FromException<T>(exception);
        }
    }
}

/// <summary>
/// Sırayla iç içe geçmiş stratejiler: ilk eklenen en dışta. Ör. <c>Timeout(toplam) → Retry → CircuitBreaker → Timeout(deneme)</c>.
/// Thread-safe ve tekrar kullanılabilir; circuit breaker gibi durumlu stratejiler için tek örnek paylaşılmalı.
/// </summary>
public sealed class ResiliencePipeline<T>
{
    private readonly ResilienceStrategy<T>[] _strategies;

    internal ResiliencePipeline(IEnumerable<ResilienceStrategy<T>> strategies) => _strategies = strategies.ToArray();

    /// <summary>Hiçbir şey yapmayan pipeline.</summary>
    public static ResiliencePipeline<T> Empty { get; } = new([]);

    public async ValueTask<T> ExecuteAsync(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        Outcome<T> outcome = await ExecuteOutcomeAsync(
                async context => Outcome.FromResult(await action(context.CancellationToken).ConfigureAwait(false)),
                new ResilienceContext(cancellationToken)
            )
            .ConfigureAwait(false);
        return outcome.GetResultOrRethrow();
    }

    public async ValueTask<T> ExecuteAsync(Func<ResilienceContext, ValueTask<T>> action, ResilienceContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        Outcome<T> outcome = await ExecuteOutcomeAsync(async c => Outcome.FromResult(await action(c).ConfigureAwait(false)), context).ConfigureAwait(false);
        return outcome.GetResultOrRethrow();
    }

    /// <summary>Sonucu exception fırlatmadan <see cref="Outcome{T}"/> olarak döner (sıcak yollarda maliyetsiz).</summary>
    public ValueTask<Outcome<T>> ExecuteOutcomeAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        Func<ResilienceContext, ValueTask<Outcome<T>>> next = c => SafeInvokeAsync(callback, c);
        for (int i = _strategies.Length - 1; i >= 0; i--)
        {
            ResilienceStrategy<T> strategy = _strategies[i];
            Func<ResilienceContext, ValueTask<Outcome<T>>> inner = next;
            next = c => strategy.ExecuteCoreAsync(inner, c);
        }

        return next(context);
    }

    private static async ValueTask<Outcome<T>> SafeInvokeAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        try
        {
            return await callback(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Outcome.FromException<T>(exception);
        }
    }
}

/// <summary>Dönüş tipinden bağımsız pipeline (yalnızca exception'lara bakan stratejiler).</summary>
public sealed class ResiliencePipeline
{
    private readonly ResiliencePipeline<object?> _inner;

    internal ResiliencePipeline(ResiliencePipeline<object?> inner) => _inner = inner;

    public static ResiliencePipeline Empty { get; } = new(ResiliencePipeline<object?>.Empty);

    public async ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        object? result = await _inner.ExecuteAsync(async ct => (object?)await action(ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return (TResult)result!;
    }

    public async ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _inner.ExecuteAsync(
                async ct =>
                {
                    await action(ct).ConfigureAwait(false);
                    return null;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async ValueTask<TResult> ExecuteAsync<TResult>(Func<ResilienceContext, ValueTask<TResult>> action, ResilienceContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        object? result = await _inner.ExecuteAsync(async c => (object?)await action(c).ConfigureAwait(false), context).ConfigureAwait(false);
        return (TResult)result!;
    }

    /// <summary>Senkron kod için (pipeline yine async çalışır; çağıran bekler).</summary>
    public TResult Execute<TResult>(Func<CancellationToken, TResult> action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(ct => ValueTask.FromResult(action(ct)), cancellationToken).AsTask().GetAwaiter().GetResult();
}
