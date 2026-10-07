using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Can.Core.BackgroundJobs;

/// <summary>Arka plan işleri için span ve metrikler (bellek içi kuyruk, tekrarlayan işler ve Hangfire ortak kullanır).</summary>
public static class BackgroundJobsTelemetry
{
    public const string Name = "Can.Core.BackgroundJobs";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>İş süresi (saniye). Etiketler: <c>can.job</c>, <c>can.outcome</c> (success / exception).</summary>
    public static readonly Histogram<double> JobDuration =
        Meter.CreateHistogram<double>("can.job.duration", unit: "s", description: "Arka plan işlerinin süresi");

    /// <summary>İş için span başlatır (dinleyen yoksa <see langword="null"/>).</summary>
    public static Activity? StartJob(string jobName, string? tenantId)
    {
        Activity? activity = Source.StartActivity($"job {jobName}", ActivityKind.Consumer);
        activity?.SetTag("can.job", jobName);
        if (tenantId is not null)
            activity?.SetTag("can.tenant", tenantId);
        return activity;
    }

    /// <summary>İşi ölçerek çalıştırır; exception'ı span'a yazıp yeniden fırlatır.</summary>
    public static async Task RunAsync(string jobName, string? tenantId, Func<Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        using Activity? activity = StartJob(jobName, tenantId);
        long started = Stopwatch.GetTimestamp();
        string outcome = "success";
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            outcome = exception is OperationCanceledException ? "canceled" : "exception";
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
        finally
        {
            activity?.SetTag("can.outcome", outcome);
            JobDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("can.job", jobName),
                new KeyValuePair<string, object?>("can.outcome", outcome)
            );
        }
    }
}
