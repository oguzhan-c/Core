using Can.Core.Application.Exceptions;
using Can.Core.Domain.Exceptions;
using Can.Core.Domain.Results;

namespace Can.Core.Application;

/// <summary>
/// <see cref="Result{TValue}"/> döndüren domain kodunu exception tabanlı handler'larda (nArchitecture tarzı) kullanmak için:
/// başarısız sonuç, hata tipine uygun exception'a çevrilir ve WebApi'nin exception middleware'i ProblemDetails'e çevirir.
/// </summary>
/// <example><code>Brand brand = Brand.Create(request.Name).OrThrow();</code></example>
public static class ResultExceptionExtensions
{
    /// <summary>Başarılıysa değeri döner; değilse ilk hatanın tipine göre exception fırlatır.</summary>
    public static TValue OrThrow<TValue>(this Result<TValue> result) =>
        result.IsSuccess ? result.Value : throw ToException(result.Errors);

    /// <inheritdoc cref="OrThrow{TValue}(Result{TValue})"/>
    public static async Task<TValue> OrThrow<TValue>(this Task<Result<TValue>> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).OrThrow();
    }

    /// <summary>
    /// Hataları exception'a çevirir: Validation → <see cref="ValidationException"/> (tüm alan hatalarıyla), NotFound → 404,
    /// Conflict → 409, Unauthorized → 401, Forbidden → 403, Failure → <see cref="BusinessException"/> (400).
    /// </summary>
    public static Exception ToException(IReadOnlyList<Error> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
            return new InvalidOperationException("Başarısız sonuçta hata yok.");

        Error first = errors[0];
        if (first.Type == ErrorType.Validation)
        {
            Dictionary<string, string[]> fields = errors
                .Where(e => e.Type == ErrorType.Validation)
                .GroupBy(e => e.Field ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
            return new ValidationException(fields) { Code = first.Code };
        }

        return first.Type switch
        {
            ErrorType.NotFound => new NotFoundException(first.Description) { Code = first.Code },
            ErrorType.Conflict => new ConflictException(first.Description) { Code = first.Code },
            ErrorType.Unauthorized => new UnauthorizedException(first.Description) { Code = first.Code },
            ErrorType.Forbidden => new ForbiddenException(first.Description) { Code = first.Code },
            ErrorType.Unexpected => new InvalidOperationException($"{first.Code}: {first.Description}"),
            _ => new BusinessException(first.Description) { Code = first.Code },
        };
    }
}
