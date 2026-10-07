using System.Runtime.ExceptionServices;

namespace Can.Core.Resilience;

/// <summary>Bir denemenin sonucu: ya değer ya exception.</summary>
public readonly struct Outcome<T>
{
    private readonly ExceptionDispatchInfo? _exception;

    private Outcome(T? result, ExceptionDispatchInfo? exception)
    {
        Result = result;
        _exception = exception;
    }

    public T? Result { get; }

    public Exception? Exception => _exception?.SourceException;

    public bool IsSuccess => _exception is null;

    public static Outcome<T> FromResult(T result) => new(result, null);

    public static Outcome<T> FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new Outcome<T>(default, ExceptionDispatchInfo.Capture(exception));
    }

    /// <summary>Exception varsa orijinal stack trace ile yeniden fırlatır; yoksa değeri döner.</summary>
    public T GetResultOrRethrow()
    {
        _exception?.Throw();
        return Result!;
    }

    public override string ToString() => IsSuccess ? $"Result: {Result}" : $"Exception: {Exception!.GetType().Name}";
}

public static class Outcome
{
    public static Outcome<T> FromResult<T>(T result) => Outcome<T>.FromResult(result);

    public static Outcome<T> FromException<T>(Exception exception) => Outcome<T>.FromException(exception);
}

/// <summary>
/// Hangi sonuçların "hata" sayılacağını tanımlar (retry, circuit breaker, fallback, hedging için):
/// <c>new PredicateBuilder&lt;HttpResponseMessage&gt;().Handle&lt;HttpRequestException&gt;().HandleResult(r =&gt; (int)r.StatusCode &gt;= 500)</c>.
/// </summary>
public sealed class PredicateBuilder<T>
{
    private readonly List<Func<Outcome<T>, bool>> _predicates = [];

    public PredicateBuilder<T> Handle<TException>()
        where TException : Exception => Handle<TException>(_ => true);

    public PredicateBuilder<T> Handle<TException>(Func<TException, bool> predicate)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicates.Add(o => o.Exception is TException e && predicate(e));
        return this;
    }

    /// <summary>İç exception'ları da (AggregateException vb.) dolaşır.</summary>
    public PredicateBuilder<T> HandleInner<TException>(Func<TException, bool>? predicate = null)
        where TException : Exception
    {
        _predicates.Add(o =>
        {
            for (Exception? e = o.Exception; e is not null; e = e.InnerException)
            {
                if (e is TException typed && (predicate is null || predicate(typed)))
                    return true;
            }

            return false;
        });
        return this;
    }

    public PredicateBuilder<T> HandleResult(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicates.Add(o => o.IsSuccess && predicate(o.Result!));
        return this;
    }

    public PredicateBuilder<T> HandleResult(T value, IEqualityComparer<T>? comparer = null) =>
        HandleResult(r => (comparer ?? EqualityComparer<T>.Default).Equals(r, value));

    public Func<Outcome<T>, bool> Build()
    {
        Func<Outcome<T>, bool>[] predicates = _predicates.ToArray();
        return outcome => predicates.Any(p => p(outcome));
    }

    public static implicit operator Func<Outcome<T>, bool>(PredicateBuilder<T> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Build();
    }
}

/// <summary>Varsayılan "hata" tanımı: iptal dışındaki her exception.</summary>
public static class DefaultPredicates
{
    public static bool HandleExceptions<T>(Outcome<T> outcome) => outcome.Exception is not null and not OperationCanceledException;
}
