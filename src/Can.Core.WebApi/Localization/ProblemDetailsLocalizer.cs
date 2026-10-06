using System.Collections.Immutable;
using Can.Core.Domain.Results;
using Can.Core.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Can.Core.WebApi.Localization;

/// <summary>
/// ProblemDetails yanıtlarını isteğin kültürüne çevirir (<c>AddCanLocalization</c> çağrılmışsa; yoksa dokunmaz):
/// başlık, hata kodu sözlükte varsa açıklama (yer tutucular hata metadata'sından), alan hataları ve <c>details</c>.
/// </summary>
internal static class ProblemDetailsLocalizer
{
    /// <summary>Result yolunda hatalar bu anahtarla <see cref="HttpContext.Items"/>'a konur (metadata kaybolmasın).</summary>
    internal static readonly object ErrorsKey = new();

    private static readonly Dictionary<string, string> TitleKeys = new(StringComparer.Ordinal)
    {
        [ProblemTitles.Validation] = "problem.validation",
        [ProblemTitles.Business] = "problem.business",
        [ProblemTitles.Unauthorized] = "problem.unauthorized",
        [ProblemTitles.Forbidden] = "problem.forbidden",
        [ProblemTitles.NotFound] = "problem.not_found",
        [ProblemTitles.Conflict] = "problem.conflict",
        [ProblemTitles.ServerError] = "problem.server_error",
        [ProblemTitles.TooManyRequests] = "problem.too_many_requests",
    };

    public static void Customize(ProblemDetailsContext context)
    {
        if (context.HttpContext.RequestServices.GetService<IStringLocalizer>() is not { } localizer)
            return;

        ProblemDetails problem = context.ProblemDetails;

        if (problem.Title is { } title && TitleKeys.TryGetValue(title, out string? titleKey))
            problem.Title = localizer.GetOrDefault(titleKey, title);

        if (context.HttpContext.Items.TryGetValue(ErrorsKey, out object? stored) && stored is ImmutableArray<Error> errors && !errors.IsDefaultOrEmpty)
        {
            LocalizeErrors(problem, errors, localizer);
            return;
        }

        // Exception yolu: kod sözlükte varsa açıklama çevrilir.
        if (problem.Extensions.TryGetValue("code", out object? code) && code is string { Length: > 0 } key)
        {
            LocalizedString text = localizer[key];
            if (!text.ResourceNotFound)
                problem.Detail = text.Value;
        }
        else if (problem.Status >= StatusCodes.Status500InternalServerError && problem.Detail == ErrorProblemResult.UnexpectedDetail)
        {
            problem.Detail = localizer.GetOrDefault("problem.unexpected", problem.Detail);
        }
    }

    private static void LocalizeErrors(ProblemDetails problem, ImmutableArray<Error> errors, IStringLocalizer localizer)
    {
        string[] described = errors.Select(localizer.Describe).ToArray();

        if (problem is HttpValidationProblemDetails validation)
        {
            validation.Errors = errors
                .Select((error, i) => (Field: error.Field ?? string.Empty, Text: described[i]))
                .GroupBy(e => e.Field, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Text).Distinct().ToArray(), StringComparer.Ordinal);
            problem.Detail = string.Join(" ", described.Distinct());
            return;
        }

        problem.Detail = described[0];
        if (problem.Extensions.ContainsKey("details"))
            problem.Extensions["details"] = errors.Select((e, i) => new { e.Code, Description = described[i] }).ToArray();
    }
}

/// <summary>Hataları ProblemDetails olarak yazar; çeviri için hataları isteğe iliştirir.</summary>
internal sealed class ErrorProblemResult : IResult, IStatusCodeHttpResult, IContentTypeHttpResult, IValueHttpResult, IValueHttpResult<ProblemDetails>
{
    internal const string UnexpectedDetail = "Beklenmeyen bir hata oluştu.";

    private readonly ImmutableArray<Error> _errors;

    public ErrorProblemResult(ImmutableArray<Error> errors, ProblemDetails problem)
    {
        _errors = errors;
        Value = problem;
    }

    public ProblemDetails Value { get; }

    object? IValueHttpResult.Value => Value;

    public int? StatusCode => Value.Status;

    public string ContentType => "application/problem+json";

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Items[ProblemDetailsLocalizer.ErrorsKey] = _errors;
        return TypedResults.Problem(Value).ExecuteAsync(httpContext);
    }
}
