using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Can.Core.Domain.Results;

/// <summary>Değer taşımayan başarılı sonuç: <c>Result&lt;Success&gt;</c>. <c>return Result.Success;</c></summary>
public readonly record struct Success;

/// <summary>Tipinden bağımsız sonuç bilgisi (pipeline davranışları ve WebApi için).</summary>
public interface IResultBase
{
    bool IsSuccess { get; }

    /// <summary>Başarılıysa boş.</summary>
    ImmutableArray<Error> Errors { get; }
}

/// <summary>
/// Bir işlemin sonucu: ya <typeparamref name="TValue"/> değeri ya da en az bir <see cref="Error"/>.
/// Beklenen hatalar (bulunamadı, iş kuralı, doğrulama) exception yerine bununla döner; exception'lar gerçekten
/// beklenmeyen durumlar (bug, altyapı hatası) için kalır.
/// </summary>
/// <remarks>
/// <para>Değer ve hatalar dönüş tipine kendiliğinden çevrilir:</para>
/// <code>
/// public Result&lt;Product&gt; Find(Guid id)
/// {
///     Product? product = ...;
///     if (product is null)
///         return ProductErrors.NotFound(id);  // Error → Result&lt;Product&gt;
///     return product;                         // Product → Result&lt;Product&gt;
/// }
/// </code>
/// <para>
/// Zincirleme: <see cref="Then{TNext}(Func{TValue, Result{TNext}})"/> (başarılıysa sonraki adım),
/// <see cref="Map{TNext}(Func{TValue, TNext})"/> (değeri dönüştür), <see cref="Ensure"/> (koşul),
/// <see cref="Match{TResult}(Func{TValue, TResult}, Func{ImmutableArray{Error}, TResult})"/> (iki yolu birleştir).
/// İlk hatada zincir durur, sonraki adımlar çalışmaz.
/// </para>
/// <para><c>default(Result&lt;T&gt;)</c> geçersizdir ve başarısız sayılır (hata: <c>result.uninitialized</c>).</para>
/// </remarks>
[JsonConverter(typeof(ResultJsonConverterFactory))]
public readonly struct Result<TValue> : IResultBase
{
    private static readonly ImmutableArray<Error> Uninitialized =
    [
        Error.Unexpected("result.uninitialized", "Sonuç oluşturulmadan kullanıldı (default(Result))."),
    ];

    private readonly TValue? _value;
    private readonly ImmutableArray<Error> _errors;
    private readonly bool _isSuccess;

    private Result(TValue value)
    {
        _value = value;
        _errors = [];
        _isSuccess = true;
    }

    private Result(ImmutableArray<Error> errors)
    {
        if (errors.IsDefaultOrEmpty)
            throw new ArgumentException("Başarısız sonuç en az bir hata içermeli.", nameof(errors));

        foreach (Error error in errors)
            ArgumentNullException.ThrowIfNull(error, nameof(errors));

        _value = default;
        _errors = errors;
        _isSuccess = false;
    }

    public bool IsSuccess => _isSuccess;

    public bool IsFailure => !_isSuccess;

    /// <summary>Başarılı sonucun değeri. Başarısız sonuçta okunursa <see cref="InvalidOperationException"/>.</summary>
    public TValue Value =>
        _isSuccess ? _value! : throw new InvalidOperationException($"Başarısız sonucun değeri okunamaz. {FirstError}");

    /// <summary>Başarılıysa değer, değilse <see langword="default"/>.</summary>
    public TValue? ValueOrDefault => _isSuccess ? _value : default;

    /// <inheritdoc />
    public ImmutableArray<Error> Errors => _isSuccess ? [] : _errors.IsDefaultOrEmpty ? Uninitialized : _errors;

    /// <summary>İlk hata. Başarılı sonuçta okunursa <see cref="InvalidOperationException"/>.</summary>
    public Error FirstError => _isSuccess ? throw new InvalidOperationException("Başarılı sonucun hatası yok.") : Errors[0];

    // ---------------------------------------------------------------- oluşturma

    public static Result<TValue> Success(TValue value) => new(value);

    public static Result<TValue> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new([error]);
    }

    public static Result<TValue> Failure(IEnumerable<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new([.. errors]);
    }

    public static implicit operator Result<TValue>(TValue value) => new(value);

    public static implicit operator Result<TValue>(Error error) => Failure(error);

    public static implicit operator Result<TValue>(Error[] errors) => Failure(errors);

    public static implicit operator Result<TValue>(List<Error> errors) => Failure(errors);

    /// <summary>Başka tipteki bir sonucun hatalarını aktarmak için: <c>if (check.IsFailure) return check.Errors;</c></summary>
    public static implicit operator Result<TValue>(ImmutableArray<Error> errors) => new(errors);

    // ---------------------------------------------------------------- okuma

    public bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        if (_isSuccess)
        {
            value = _value!;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetValue([MaybeNullWhen(false)] out TValue value, out ImmutableArray<Error> errors)
    {
        errors = Errors;
        return TryGetValue(out value);
    }

    /// <summary>
    /// Başarısızsa <see cref="ResultFailedException"/> fırlatır. Hatanın imkânsız olması gereken yerler için
    /// (seed, testler, iç tutarlılık); beklenen hatalarda kullanma.
    /// </summary>
    public TValue ThrowIfFailure() => _isSuccess ? _value! : throw new ResultFailedException(Errors);

    // ---------------------------------------------------------------- zincirleme

    /// <summary>Başarılıysa sonraki adımı çalıştırır (sonraki adım da başarısız olabilir).</summary>
    public Result<TNext> Then<TNext>(Func<TValue, Result<TNext>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return _isSuccess ? next(_value!) : Errors;
    }

    /// <summary>Başarılıysa değeri dönüştürür.</summary>
    public Result<TNext> Map<TNext>(Func<TValue, TNext> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return _isSuccess ? Result<TNext>.Success(map(_value!)) : Errors;
    }

    /// <summary>Başarılıysa koşulu kontrol eder; sağlanmazsa <paramref name="error"/>.</summary>
    public Result<TValue> Ensure(Func<TValue, bool> predicate, Error error)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(error);
        return !_isSuccess || predicate(_value!) ? this : error;
    }

    /// <summary>Başarılıysa yan etki çalıştırır (loglama, olay ...); sonuç değişmez.</summary>
    public Result<TValue> Tap(Action<TValue> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_isSuccess)
            action(_value!);

        return this;
    }

    /// <summary>Başarısızsa hatalardan bir yedek değer üretir.</summary>
    public Result<TValue> Else(Func<ImmutableArray<Error>, TValue> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return _isSuccess ? this : fallback(Errors);
    }

    /// <summary>Başarısızsa yedek değer.</summary>
    public Result<TValue> Else(TValue fallback) => _isSuccess ? this : fallback;

    /// <summary>Hataları dönüştürür (ör. alt katmanın hatasını üst katmanın diline çevirmek için).</summary>
    public Result<TValue> MapErrors(Func<Error, Error> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return _isSuccess ? this : Errors.Select(map).ToArray();
    }

    /// <summary>Değeri at, yalnızca başarı/hata bilgisini koru.</summary>
    public Result<Success> ToSuccess() => _isSuccess ? Result.Success : Errors;

    public Task<Result<TNext>> ThenAsync<TNext>(Func<TValue, Task<Result<TNext>>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return _isSuccess ? next(_value!) : Task.FromResult<Result<TNext>>(Errors);
    }

    public async Task<Result<TNext>> MapAsync<TNext>(Func<TValue, Task<TNext>> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return _isSuccess ? await map(_value!).ConfigureAwait(false) : Errors;
    }

    public async Task<Result<TValue>> EnsureAsync(Func<TValue, Task<bool>> predicate, Error error)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(error);
        return !_isSuccess || await predicate(_value!).ConfigureAwait(false) ? this : error;
    }

    public async Task<Result<TValue>> TapAsync(Func<TValue, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_isSuccess)
            await action(_value!).ConfigureAwait(false);

        return this;
    }

    // ---------------------------------------------------------------- sonlandırma

    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<ImmutableArray<Error>, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return _isSuccess ? onSuccess(_value!) : onFailure(Errors);
    }

    /// <summary>Yalnızca ilk hatayla ilgilenenler için.</summary>
    public TResult MatchFirst<TResult>(Func<TValue, TResult> onSuccess, Func<Error, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return _isSuccess ? onSuccess(_value!) : onFailure(FirstError);
    }

    public void Switch(Action<TValue> onSuccess, Action<ImmutableArray<Error>> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);

        if (_isSuccess)
            onSuccess(_value!);
        else
            onFailure(Errors);
    }

    public override string ToString() =>
        _isSuccess ? $"Success({_value})" : $"Failure({string.Join("; ", Errors.Select(e => e.ToString()))})";
}

/// <summary>Sonuç oluşturma yardımcıları.</summary>
public static class Result
{
    /// <summary>Değersiz başarı: <c>return Result.Success;</c> (dönüş tipi <c>Result&lt;Success&gt;</c>).</summary>
    public static Success Success => default;

    public static Result<TValue> Ok<TValue>(TValue value) => Result<TValue>.Success(value);

    public static Result<TValue> Fail<TValue>(Error error) => Result<TValue>.Failure(error);

    /// <summary>
    /// Birden fazla kontrolü tek seferde yapar ve TÜM hataları toplar (null = kontrol geçti):
    /// <code>
    /// Result&lt;Success&gt; valid = Result.Validate(
    ///     Check.Required(name, "Ad", 50),
    ///     Check.NotNegative(price, "Fiyat"));
    /// if (valid.IsFailure)
    ///     return valid.Errors;
    /// </code>
    /// </summary>
    public static Result<Success> Validate(params ReadOnlySpan<Error?> checks)
    {
        ImmutableArray<Error>.Builder? errors = null;

        foreach (Error? error in checks)
        {
            if (error is not null)
                (errors ??= ImmutableArray.CreateBuilder<Error>()).Add(error);
        }

        return errors is null ? Success : errors.ToImmutable();
    }

    /// <summary>Sonuçların hepsi başarılıysa başarı; değilse hepsinin hataları birlikte.</summary>
    public static Result<Success> Combine(params ReadOnlySpan<IResultBase> results)
    {
        ImmutableArray<Error>.Builder? errors = null;

        foreach (IResultBase result in results)
        {
            if (!result.IsSuccess)
                (errors ??= ImmutableArray.CreateBuilder<Error>()).AddRange(result.Errors);
        }

        return errors is null ? Success : errors.ToImmutable();
    }
}

/// <summary><see cref="Result{TValue}.ThrowIfFailure"/> ile başarısız sonuç exception'a çevrildiğinde.</summary>
public sealed class ResultFailedException : Exception
{
    public ResultFailedException(ImmutableArray<Error> errors)
        : base("Sonuç başarısız: " + string.Join("; ", errors.Select(e => e.ToString())))
    {
        Errors = errors;
    }

    public ImmutableArray<Error> Errors { get; }
}
