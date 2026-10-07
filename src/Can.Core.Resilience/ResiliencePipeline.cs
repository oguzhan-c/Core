using System.Collections.Concurrent;

namespace Can.Core.Resilience;

/// <summary>Bir strateji: işi (<c>callback</c>) sarar; tekrar dener, keser, sınırlar ...</summary>
/// <remarks>
/// <c>callback</c> exception fırlatmaz: kullanıcının işindeki hatalar zincirin en içinde <see cref="Outcome{T}"/>'a çevrilir.
/// </remarks>
public abstract class ResilienceStrategy<T>
{
    protected internal abstract ValueTask<Outcome<T>> ExecuteCoreAsync(
        Func<ResilienceContext, ValueTask<Outcome<T>>> callback,
        ResilienceContext context);

    /// <summary>İşi çalıştırıp exception'ı sonuca çevirir (kendi stratejin işi doğrudan çağırıyorsa).</summary>
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
/// Strateji zinciri bir kez kurulur; çalıştırılacak iş bağlamda taşınır (çağrı başına delegate/closure yok), bağlamlar
/// havuzdan alınır, senkron tamamlanan işler bekleme makinesi oluşturmadan geçer.
/// </remarks>
public sealed class ResiliencePipeline<T>
{
    private static readonly Func<ResilienceContext, ValueTask<Outcome<T>>> Innermost = InvokeExecutionCallback;

    private readonly Func<ResilienceContext, ValueTask<Outcome<T>>> _chain;
    private readonly bool _isEmpty;

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
        _isEmpty = array.Length == 0;
    }

    /// <summary>Hiçbir şey yapmayan pipeline.</summary>
    public static ResiliencePipeline<T> Empty { get; } = new([]);

    public ValueTask<T> ExecuteAsync(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Strateji yoksa sarmalamaya gerek yok.
        return _isEmpty ? action(cancellationToken) : ExecuteWithStateAsync(Callbacks.Token, action, cancellationToken);
    }

    public async ValueTask<T> ExecuteAsync(Func<ResilienceContext, ValueTask<T>> action, ResilienceContext context)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);

        object? previousState = context.State;
        context.State = action;
        try
        {
            Outcome<T> outcome = await ExecuteOutcomeAsync(Callbacks.Context, context).ConfigureAwait(false);
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

    /// <summary>
    /// Havuzdan bağlam alıp <paramref name="state"/>'i (kullanıcının işi) taşıyarak çalıştırır; <paramref name="callback"/>
    /// statik olmalı (closure değil).
    /// </summary>
    internal async ValueTask<T> ExecuteWithStateAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, object state, CancellationToken cancellationToken)
    {
        ResilienceContext context = ResilienceContextPool.Rent(cancellationToken);
        try
        {
            context.State = state;
            context.ExecutionCallback = callback;
            Outcome<T> outcome = await _chain(context).ConfigureAwait(false);
            return outcome.GetResultOrRethrow();
        }
        finally
        {
            ResilienceContextPool.Return(context);
        }
    }

    /// <summary>Zincirin en içi: kullanıcının işini çalıştırır, hatayı sonuca çevirir. Senkron tamamlanırsa beklemez.</summary>
    private static ValueTask<Outcome<T>> InvokeExecutionCallback(ResilienceContext context)
    {
        ValueTask<Outcome<T>> pending;
        try
        {
            pending = ((Func<ResilienceContext, ValueTask<Outcome<T>>>)context.ExecutionCallback!)(context);
        }
        catch (Exception exception)
        {
            return new ValueTask<Outcome<T>>(Outcome.FromException<T>(exception));
        }

        return pending.IsCompletedSuccessfully ? pending : AwaitSafeAsync(pending);

        static async ValueTask<Outcome<T>> AwaitSafeAsync(ValueTask<Outcome<T>> pending)
        {
            try
            {
                return await pending.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return Outcome.FromException<T>(exception);
            }
        }
    }

    /// <summary>Statik callback'ler: kullanıcının işini bağlamın <c>State</c>'inden okur.</summary>
    internal static class Callbacks
    {
        public static readonly Func<ResilienceContext, ValueTask<Outcome<T>>> Token = static context =>
            Wrap(((Func<CancellationToken, ValueTask<T>>)context.State!)(context.CancellationToken));

        public static readonly Func<ResilienceContext, ValueTask<Outcome<T>>> Context = static context =>
            Wrap(((Func<ResilienceContext, ValueTask<T>>)context.State!)(context));

        /// <summary>Sonucu <see cref="Outcome{T}"/>'a sarar; senkron tamamlandıysa bekleme makinesi oluşturmaz.</summary>
        public static ValueTask<Outcome<T>> Wrap(ValueTask<T> pending)
        {
            return pending.IsCompletedSuccessfully ? new ValueTask<Outcome<T>>(Outcome.FromResult(pending.Result)) : AwaitAsync(pending);

            static async ValueTask<Outcome<T>> AwaitAsync(ValueTask<T> pending) => Outcome.FromResult(await pending.ConfigureAwait(false));
        }
    }
}

/// <summary>
/// Dönüş tipinden bağımsız pipeline. Her sonuç tipi için tipli bir zincir kurulur ve saklanır (kutulama yok);
/// circuit breaker / rate limiter durumu tüm tipler arasında ortaktır.
/// </summary>
public sealed class ResiliencePipeline
{
    private readonly IStrategyFactory[] _factories;
    private readonly ConcurrentDictionary<Type, object> _typed = new();

    internal ResiliencePipeline(IStrategyFactory[] factories) => _factories = factories;

    public static ResiliencePipeline Empty { get; } = new([]);

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> action, CancellationToken cancellationToken = default) =>
        Typed<TResult>().ExecuteAsync(action, cancellationToken);

    public ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_factories.Length == 0)
            return action(cancellationToken);

        ValueTask<VoidResult> pending = Typed<VoidResult>().ExecuteWithStateAsync(VoidCallback, action, cancellationToken);
        return pending.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(pending.AsTask());
    }

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<ResilienceContext, ValueTask<TResult>> action, ResilienceContext context) =>
        Typed<TResult>().ExecuteAsync(action, context);

    /// <summary>Senkron kod için (pipeline yine async çalışır; çağıran bekler).</summary>
    public TResult Execute<TResult>(Func<CancellationToken, TResult> action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(ct => ValueTask.FromResult(action(ct)), cancellationToken).AsTask().GetAwaiter().GetResult();

    internal ResiliencePipeline<TResult> Typed<TResult>() =>
        (ResiliencePipeline<TResult>)_typed.GetOrAdd(
            typeof(TResult),
            static (_, factories) => new ResiliencePipeline<TResult>(factories.Select(f => f.Create<TResult>())),
            _factories
        );

    private static readonly Func<ResilienceContext, ValueTask<Outcome<VoidResult>>> VoidCallback = static context =>
    {
        ValueTask pending = ((Func<CancellationToken, ValueTask>)context.State!)(context.CancellationToken);
        return pending.IsCompletedSuccessfully ? new ValueTask<Outcome<VoidResult>>(Outcome.FromResult(default(VoidResult))) : AwaitAsync(pending);

        static async ValueTask<Outcome<VoidResult>> AwaitAsync(ValueTask pending)
        {
            await pending.ConfigureAwait(false);
            return Outcome.FromResult(default(VoidResult));
        }
    };

    private readonly struct VoidResult;
}
