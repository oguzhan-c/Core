using System.Collections.Immutable;
using System.Reflection;

namespace Can.Core.Domain.Results;

/// <summary>
/// <c>Task&lt;Result&lt;T&gt;&gt;</c> üzerinde zincirleme (her adımda <c>await</c> yazmadan) ve null değerleri sonuca çevirme.
/// </summary>
/// <example>
/// <code>
/// return await _products.GetByIdAsync(id)
///     .ToResult(ProductErrors.NotFound(id))
///     .Then(product =&gt; product.ChangePrice(newPrice))
///     .Map(_ =&gt; Result.Success);
/// </code>
/// </example>
public static class ResultExtensions
{
    // ---------------------------------------------------------------- null → sonuç

    /// <summary>Değer null ise <paramref name="error"/>, değilse başarı.</summary>
    public static Result<T> ToResult<T>(this T? value, Error error)
        where T : class => value is null ? error : value;

    /// <inheritdoc cref="ToResult{T}(T, Error)"/>
    public static Result<T> ToResult<T>(this T? value, Error error)
        where T : struct => value is null ? error : value.Value;

    /// <summary>Bekleyip null kontrolü yapar: <c>await repo.GetAsync(...).ToResult(ProductErrors.NotFound(id))</c>.</summary>
    public static async Task<Result<T>> ToResult<T>(this Task<T?> task, Error error)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).ToResult(error);
    }

    // ---------------------------------------------------------------- Task<Result<T>> zincirleri

    public static async Task<Result<TNext>> Then<T, TNext>(this Task<Result<T>> task, Func<T, Result<TNext>> next)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).Then(next);
    }

    public static async Task<Result<TNext>> ThenAsync<T, TNext>(this Task<Result<T>> task, Func<T, Task<Result<TNext>>> next)
    {
        ArgumentNullException.ThrowIfNull(task);
        return await (await task.ConfigureAwait(false)).ThenAsync(next).ConfigureAwait(false);
    }

    public static async Task<Result<TNext>> Map<T, TNext>(this Task<Result<T>> task, Func<T, TNext> map)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).Map(map);
    }

    public static async Task<Result<TNext>> MapAsync<T, TNext>(this Task<Result<T>> task, Func<T, Task<TNext>> map)
    {
        ArgumentNullException.ThrowIfNull(task);
        return await (await task.ConfigureAwait(false)).MapAsync(map).ConfigureAwait(false);
    }

    public static async Task<Result<T>> Ensure<T>(this Task<Result<T>> task, Func<T, bool> predicate, Error error)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).Ensure(predicate, error);
    }

    public static async Task<Result<T>> Tap<T>(this Task<Result<T>> task, Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).Tap(action);
    }

    public static async Task<Result<T>> TapAsync<T>(this Task<Result<T>> task, Func<T, Task> action)
    {
        ArgumentNullException.ThrowIfNull(task);
        return await (await task.ConfigureAwait(false)).TapAsync(action).ConfigureAwait(false);
    }

    public static async Task<Result<T>> Else<T>(this Task<Result<T>> task, Func<ImmutableArray<Error>, T> fallback)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).Else(fallback);
    }

    public static async Task<Result<Success>> ToSuccess<T>(this Task<Result<T>> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).ToSuccess();
    }

    public static async Task<TResult> Match<T, TResult>(
        this Task<Result<T>> task,
        Func<T, TResult> onSuccess,
        Func<ImmutableArray<Error>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).Match(onSuccess, onFailure);
    }
}

/// <summary>
/// Tipini derleme anında bilmeyen kod (ör. mediator pipeline davranışları: <c>TResponse</c>) için:
/// yanıt tipi bir <see cref="Result{TValue}"/> mı, öyleyse hatalardan başarısız sonuç üret.
/// </summary>
public static class ResultTypes
{
    /// <summary><paramref name="type"/> bir <c>Result&lt;T&gt;</c> mi?</summary>
    public static bool IsResult(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>);
    }

    /// <summary><typeparamref name="TResponse"/> bir <c>Result&lt;T&gt;</c> ise hatalarla başarısız sonuç üretir.</summary>
    public static bool TryCreateFailure<TResponse>(IEnumerable<Error> errors, out TResponse response)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (FailureFactory<TResponse>.Create is { } create)
        {
            response = create(errors);
            return true;
        }

        response = default!;
        return false;
    }

    private static class FailureFactory<TResponse>
    {
        public static readonly Func<IEnumerable<Error>, TResponse>? Create = Build();

        private static Func<IEnumerable<Error>, TResponse>? Build()
        {
            if (!IsResult(typeof(TResponse)))
                return null;

            MethodInfo method = typeof(TResponse).GetMethod(
                nameof(Result<object>.Failure),
                BindingFlags.Public | BindingFlags.Static,
                [typeof(IEnumerable<Error>)]
            )!;

            return method.CreateDelegate<Func<IEnumerable<Error>, TResponse>>();
        }
    }
}
