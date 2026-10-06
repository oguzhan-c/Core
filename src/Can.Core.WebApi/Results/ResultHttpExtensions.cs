using System.Collections.Immutable;
using Can.Core.Domain.Results;
using Can.Core.WebApi.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Can.Core.WebApi;

/// <summary>
/// <see cref="Result{TValue}"/>'ı HTTP yanıtına çevirir. Hatalar, exception handler'ın ürettiğiyle AYNI biçimde
/// ProblemDetails olur (<c>status</c>, <c>title</c>, <c>detail</c>, <c>code</c>; doğrulamada <c>errors</c>), böylece
/// istemci iki yolu ayırt etmek zorunda kalmaz.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>Başarı</term><description>200 + değer; değer <see cref="Success"/> ise 204</description></item>
/// <item><term>Validation</term><description>400, alan bazında <c>errors</c></description></item>
/// <item><term>Failure</term><description>400</description></item>
/// <item><term>Unauthorized / Forbidden</term><description>401 / 403</description></item>
/// <item><term>NotFound / Conflict</term><description>404 / 409</description></item>
/// <item><term>Unexpected</term><description>500</description></item>
/// </list>
/// Birden fazla hata varsa durum kodunu ilk hata belirler.
/// </remarks>
/// <example>
/// <code>
/// group.MapGet("/{id:guid}", (Guid id, ISender sender, CancellationToken ct) =&gt;
///     sender.Send(new GetProductQuery(id), ct).ToHttpResult());
///
/// group.MapPost("/", (CreateProductCommand command, ISender sender, CancellationToken ct) =&gt;
///     sender.Send(command, ct).ToHttpResult(product =&gt; TypedResults.Created($"/api/products/{product.Id}", product)));
/// </code>
/// </example>
public static class ResultHttpExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result) =>
        result.IsSuccess ? SuccessResult(result.Value) : result.Errors.ToProblem();

    /// <summary>Başarıda özel yanıt (ör. 201 Created, cookie yazma); hatada ProblemDetails.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : result.Errors.ToProblem();
    }

    public static async Task<IResult> ToHttpResult<T>(this Task<Result<T>> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).ToHttpResult();
    }

    public static async Task<IResult> ToHttpResult<T>(this Task<Result<T>> task, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(task);
        return (await task.ConfigureAwait(false)).ToHttpResult(onSuccess);
    }

    /// <summary>Tek hatayı ProblemDetails yanıtına çevirir (ör. endpoint'te handler'a gitmeden dönen hata).</summary>
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ToProblem([error]);
    }

    /// <summary>Hataları ProblemDetails yanıtına çevirir.</summary>
    /// <remarks><c>AddCanLocalization</c> çağrıldıysa başlık ve açıklamalar isteğin diline çevrilir (hata kodu → metin).</remarks>
    public static IResult ToProblem(this ImmutableArray<Error> errors)
    {
        if (errors.IsDefaultOrEmpty)
            errors = [Error.Unexpected()];

        return new ErrorProblemResult(errors, CreateProblemDetails(errors));
    }

    /// <summary>Hatalardan ProblemDetails üretir (kendi yanıtını yazan kod için).</summary>
    public static ProblemDetails CreateProblemDetails(ImmutableArray<Error> errors)
    {
        if (errors.IsDefaultOrEmpty)
            errors = [Error.Unexpected()];

        Error first = errors[0];
        ProblemDetails problem;

        if (errors.All(e => e.Type == ErrorType.Validation))
        {
            Dictionary<string, string[]> fields = errors
                .GroupBy(e => e.Field ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).Distinct().ToArray(), StringComparer.Ordinal);

            problem = new HttpValidationProblemDetails(fields) { Detail = string.Join(" ", errors.Select(e => e.Description).Distinct()) };
        }
        else
        {
            problem = new ProblemDetails { Detail = first.Description };

            // Birden fazla iş hatası varsa hepsi (kod + açıklama) "details" altında listelenir
            // ("errors" doğrulama hatalarına ayrılmış: alan → mesajlar).
            if (errors.Length > 1)
                problem.Extensions["details"] = errors.Select(e => new { e.Code, e.Description }).ToArray();
        }

        (problem.Status, problem.Title) = StatusOf(first.Type);
        problem.Extensions["code"] = first.Code;
        return problem;
    }

    /// <summary>Hata türünün HTTP durum kodu ve başlığı.</summary>
    public static (int Status, string Title) StatusOf(ErrorType type) =>
        type switch
        {
            ErrorType.Validation => (StatusCodes.Status400BadRequest, ProblemTitles.Validation),
            ErrorType.Failure => (StatusCodes.Status400BadRequest, ProblemTitles.Business),
            ErrorType.Unauthorized => (StatusCodes.Status401Unauthorized, ProblemTitles.Unauthorized),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, ProblemTitles.Forbidden),
            ErrorType.NotFound => (StatusCodes.Status404NotFound, ProblemTitles.NotFound),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, ProblemTitles.Conflict),
            _ => (StatusCodes.Status500InternalServerError, ProblemTitles.ServerError),
        };

    private static IResult SuccessResult<T>(T value) => value is Success ? TypedResults.NoContent() : TypedResults.Ok(value);
}

/// <summary>ProblemDetails başlıkları (exception handler ve Result eşlemesi aynı metni kullanır).</summary>
public static class ProblemTitles
{
    public const string Validation = "Doğrulama hatası";
    public const string Business = "İş kuralı ihlali";
    public const string Unauthorized = "Giriş gerekli";
    public const string Forbidden = "Yetki yok";
    public const string NotFound = "Bulunamadı";
    public const string Conflict = "Çakışma";
    public const string ServerError = "Sunucu hatası";
    public const string TooManyRequests = "Çok fazla istek";
}
