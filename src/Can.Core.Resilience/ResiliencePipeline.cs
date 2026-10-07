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
/// <remarks>
/// Strateji zinciri kurulurken bir kez oluşturulur; çalıştırılacak iş bağlamda taşınır. Böylece her çağrıda yeni
/// delegate/closure oluşmaz ve bağlamlar havuzdan alınır.
/// </remarks>
public sealed class ResiliencePipeline<T>
{
    private static readonly Func<ResilienceContext, ValueTask<Outcome<T>>> Innermost = InvokeExecutionCallbackAsync;

    private static readonly Func<ResilienceContext, ValueTask<Outcome<T>>> TokenCallback = static async context =>
        Outcome.FromResult(await ((Func<CancellationToken, ValueTask<T>>)context.State!)(context.CancellationToken).ConfigureAwait(false));

    private static readonly Func<ResilienceContext, ValueTask<Outcome<T>>> ContextCallback = static async context =>
        Outcome.FromResult(await ((Func<ResilienceContext, ValueTask<T>>)context.State!)(context).ConfigureAwait(false));

    private readonly Func<ResilienceContext, ValueTask<Outcome<T>>> _chain;

    internal ResiliencePipeline(IEnumerable<ResilienceStrategy<T>> strategies)
    {
        ResilienceStrategy<T>[] array = strategies.ToArray();
        Func<ResilienceContext, ValueTask<Outcome<T>>> next = Innermost;
        for (int i = array.Length - 1; i >= 0; i--)
        {
            ResilienceStrategy<T> strategy = array[i];
            Func<ResilienceContext, ValueTask<Outcome<T>>> inner = next;
            next = context => strategy.ExecuteCoreAsync(inner, context);
        }

        _chain = next;
        IsEmpty = array.Length == 0;
    }

    /// <summary>Hiçbir şey yapmayan pipeline.</summary>
    public static ResiliencePipeline<T> Empty { get; } = new([]);

    internal bool IsEmpty { get; }

    public async ValueTask<T> ExecuteAsync(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        ResilienceContext context = ResilienceContextPool.Rent(cancellationToken);
        try
        {
            context.State = action;
            context.ExecutionCallback = TokenCallback;
            Outcome<T> outcome = await _chain(context).ConfigureAwait(false);
            return outcome.GetResultOrRethrow();
        }
        finally
        {
            ResilienceContextPool.Return(context);
        }
    }

    public async ValueTask<T> ExecuteAsync(Func<ResilienceContext, ValueTask<T>> action, ResilienceContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        object? previousState = context.State;
        context.State = action;
        try
        {
            Outcome<T> outcome = await ExecuteOutcomeAsync(ContextCallback, context).ConfigureAwait(false);
            return outcome.GetResultOrRethrow();
        }
        finally
        {
            context.State = previousState;
        }
    }

    /// <summary>Sonucu exception fırlatmadan <see cref="Outcome{T}"/> olarak döner.</summary>
    public async ValueTask<Outcome<T>> ExecuteOutcomeAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        // İç içe pipeline'lar aynı bağlamı kullanabilir; dıştakinin işini geri koy.
        object? previous = context.ExecutionCallback;
        context.ExecutionCallback = callback;
        try
        {
            return await _chain(context).ConfigureAwait(false);
        }
        finally
        {
            context.ExecutionCallback = previous;
        }
    }

    private static async ValueTask<Outcome<T>> InvokeExecutionCallbackAsync(ResilienceContext context)
    {
        try
        {
            return await ((Func<ResilienceContext, ValueTask<Outcome<T>>>)context.ExecutionCallback!)(context).ConfigureAwait(false);
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

        ResilienceContext context = ResilienceContextPool.Rent(cancellationToken);
        try
        {
            context.State = action;
            Outcome<object?> outcome = await _inner.ExecuteOutcomeAsync(Callbacks<TResult>.Token, context).ConfigureAwait(false);
            return (TResult)outcome.GetResultOrRethrow()!;
        }
        finally
        {
            ResilienceContextPool.Return(context);
        }
    }

    public async ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        ResilienceContext context = ResilienceContextPool.Rent(cancellationToken);
        try
        {
            context.State = action;
            Outcome<object?> outcome = await _inner.ExecuteOutcomeAsync(VoidCallback, context).ConfigureAwait(false);
            outcome.GetResultOrRethrow();
        }
        finally
        {
            ResilienceContextPool.Return(context);
        }
    }

    public async ValueTask<TResult> ExecuteAsync<TResult>(Func<ResilienceContext, ValueTask<TResult>> action, ResilienceContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        object? previousState = context.State;
        context.State = action;
        try
        {
            Outcome<object?> outcome = await _inner.ExecuteOutcomeAsync(Callbacks<TResult>.Context, context).ConfigureAwait(false);
            return (TResult)outcome.GetResultOrRethrow()!;
        }
        finally
        {
            context.State = previousState;
        }
    }

    /// <summary>Senkron kod için (pipeline yine async çalışır; çağıran bekler).</summary>
    public TResult Execute<TResult>(Func<CancellationToken, TResult> action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(ct => ValueTask.FromResult(action(ct)), cancellationToken).AsTask().GetAwaiter().GetResult();

    private static readonly Func<ResilienceContext, ValueTask<Outcome<object?>>> VoidCallback = static async context =>
    {
        await ((Func<CancellationToken, ValueTask>)context.State!)(context.CancellationToken).ConfigureAwait(false);
        return Outcome.FromResult<object?>(null);
    };

    /// <summary>Tip başına bir kez oluşturulan statik callback'ler (çağrı başına closure yok).</summary>
    private static class Callbacks<TResult>
    {
        public static readonly Func<ResilienceContext, ValueTask<Outcome<object?>>> Token = static async context =>
            Outcome.FromResult<object?>(await ((Func<CancellationToken, ValueTask<TResult>>)context.State!)(context.CancellationToken).ConfigureAwait(false));

        public static readonly Func<ResilienceContext, ValueTask<Outcome<object?>>> Context = static async context =>
            Outcome.FromResult<object?>(await ((Func<ResilienceContext, ValueTask<TResult>>)context.State!)(context).ConfigureAwait(false));
    }
}
