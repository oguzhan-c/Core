using System.Diagnostics;
using System.Diagnostics.Metrics;
using Can.Core.Domain.Results;
using Can.Core.Mediator;

namespace Can.Core.Application.Behaviors;

/// <summary>
/// İstek telemetrisi (paket gerektirmez; .NET'in <see cref="ActivitySource"/> ve <see cref="Meter"/> API'leri).
/// Dinleyen yoksa (OpenTelemetry vb. kayıtlı değilse) maliyeti yok denecek kadar azdır.
/// </summary>
public static class ApplicationTelemetry
{
    public const string Name = "Can.Core.Application";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>İstek süresi (saniye). Etiketler: <c>can.request</c>, <c>can.outcome</c> (success / failure / exception).</summary>
    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("can.request.duration", unit: "s", description: "Mediator isteklerinin süresi");
}

/// <summary>
/// Her istek için bir span (adı istek tipinin adı) ve süre ölçümü. En dıştaki behavior'dır; böylece yetki, doğrulama,
/// önbellek ve transaction dahil tüm süre ölçülür. <c>Result</c> hataları "failure", exception'lar "exception" olarak işaretlenir.
/// </summary>
public sealed class TelemetryBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly string RequestName = typeof(TRequest).Name;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!ApplicationTelemetry.Source.HasListeners() && !ApplicationTelemetry.RequestDuration.Enabled)
            return await next().ConfigureAwait(false);

        using Activity? activity = ApplicationTelemetry.Source.StartActivity(RequestName);
        activity?.SetTag("can.request", RequestName);

        long started = Stopwatch.GetTimestamp();
        string outcome = "success";
        try
        {
            TResponse response = await next().ConfigureAwait(false);

            if (response is IResultBase { IsSuccess: false } failed)
            {
                // Beklenen iş hatası: span hata değildir, ama kod etiketlenir.
                outcome = "failure";
                activity?.SetTag("can.error.code", failed.Errors[0].Code);
                activity?.SetTag("can.error.type", failed.Errors[0].Type.ToString());
            }

            return response;
        }
        catch (Exception exception)
        {
            outcome = "exception";
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
        finally
        {
            activity?.SetTag("can.outcome", outcome);
            ApplicationTelemetry.RequestDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("can.request", RequestName),
                new KeyValuePair<string, object?>("can.outcome", outcome)
            );
        }
    }
}
