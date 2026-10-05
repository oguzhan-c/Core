using Can.Core.Mediator;
using Microsoft.Extensions.Logging;

namespace Can.Core.Application.Behaviors;

/// <summary>
/// Tüm istekleri ölçer; <see cref="CanApplicationOptions.SlowRequestThreshold"/>'dan uzun sürenleri uyarı olarak loglar.
/// </summary>
public sealed class PerformanceBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly CanApplicationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PerformanceBehavior<TRequest, TResponse>> _logger;

    public PerformanceBehavior(
        CanApplicationOptions options,
        TimeProvider timeProvider,
        ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
    {
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        long start = _timeProvider.GetTimestamp();

        TResponse response = await next().ConfigureAwait(false);

        TimeSpan elapsed = _timeProvider.GetElapsedTime(start);
        if (elapsed > _options.SlowRequestThreshold)
        {
            _logger.LogWarning(
                "Yavaş istek: {RequestName} {ElapsedMilliseconds} ms sürdü (eşik {ThresholdMilliseconds} ms).",
                typeof(TRequest).Name,
                (long)elapsed.TotalMilliseconds,
                (long)_options.SlowRequestThreshold.TotalMilliseconds
            );
        }

        return response;
    }
}
