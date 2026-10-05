using Can.Core.Mediator;
using FluentValidation;
using FluentValidation.Results;
using ValidationException = Can.Core.Application.Exceptions.ValidationException;

namespace Can.Core.Application.Behaviors;

/// <summary>
/// İstek için kayıtlı tüm FluentValidation validator'larını (async kurallar dahil) çalıştırır; hata varsa
/// alan bazlı hatalarla <see cref="ValidationException"/> fırlatır. Validator'ı olmayan istekler etkilenmez.
/// </summary>
public sealed class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IValidator<TRequest>[] _validators;

    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators.ToArray();
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (_validators.Length == 0)
            return await next().ConfigureAwait(false);

        var context = new ValidationContext<TRequest>(request);
        var failures = new List<ValidationFailure>();

        foreach (IValidator<TRequest> validator in _validators)
        {
            ValidationResult result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            failures.AddRange(result.Errors);
        }

        if (failures.Count > 0)
        {
            Dictionary<string, string[]> errors = failures
                .GroupBy(f => f.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray());

            throw new ValidationException(errors);
        }

        return await next().ConfigureAwait(false);
    }
}
