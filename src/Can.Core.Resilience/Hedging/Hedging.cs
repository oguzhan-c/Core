namespace Can.Core.Resilience;

public readonly record struct OnHedgingArguments(ResilienceContext Context, int AttemptNumber);

public readonly record struct HedgingActionArguments<T>(ResilienceContext Context, int AttemptNumber, Func<ResilienceContext, ValueTask<Outcome<T>>> Callback);

/// <summary>
/// Yanıt gecikirse (ya da hata gelirse) aynı işi paralel olarak tekrar başlatır; ilk kabul edilebilir sonuç kazanır,
/// diğerleri iptal edilir. Kuyruk gecikmesini (p99) düşürür; işin idempotent olması gerekir.
/// </summary>
public sealed class HedgingOptions<T>
{
    /// <summary>İlk denemeye ek en fazla kaç paralel deneme.</summary>
    public int MaxHedgedAttempts { get; set; } = 1;

    /// <summary>
    /// Yeni deneme başlatmadan önce beklenen süre. <see cref="TimeSpan.Zero"/>: hepsi aynı anda;
    /// <see cref="Timeout.InfiniteTimeSpan"/>: yalnızca önceki deneme hata verince (geç kalınca değil).
    /// </summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    public Func<Outcome<T>, bool> ShouldHandle { get; set; } = DefaultPredicates.HandleExceptions;

    /// <summary>Ek denemelerde farklı bir iş çalıştır (ör. başka bir bölgeye istek); boşsa aynı iş.</summary>
    public Func<HedgingActionArguments<T>, Func<ValueTask<Outcome<T>>>>? ActionGenerator { get; set; }

    public Func<OnHedgingArguments, ValueTask>? OnHedging { get; set; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHedgedAttempts, 1);
        ArgumentNullException.ThrowIfNull(ShouldHandle);
        if (Delay != Timeout.InfiniteTimeSpan)
            ArgumentOutOfRangeException.ThrowIfLessThan(Delay, TimeSpan.Zero);
    }
}

internal sealed class HedgingStrategy<T>(HedgingOptions<T> options, TimeProvider timeProvider) : ResilienceStrategy<T>
{
    protected internal override async ValueTask<Outcome<T>> ExecuteCoreAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        var running = new List<(Task<Outcome<T>> Task, CancellationTokenSource Cancellation)>();
        int started = 0;
        Outcome<T>? last = null;

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        try
        {
            await StartAsync().ConfigureAwait(false);

            while (true)
            {
                bool canHedge = started <= options.MaxHedgedAttempts;
                Task delay = canHedge && options.Delay != Timeout.InfiniteTimeSpan
                    ? Task.Delay(options.Delay, timeProvider, delayCancellation.Token)
                    : Task.Delay(Timeout.Infinite, delayCancellation.Token);

                if (running.Count == 0)
                    return last ?? Outcome.FromException<T>(new InvalidOperationException("Hedging hiç deneme çalıştırmadı."));

                Task completed = await Task.WhenAny(running.Select(r => (Task)r.Task).Append(delay)).ConfigureAwait(false);

                if (completed == delay)
                {
                    if (context.CancellationToken.IsCancellationRequested)
                        return Outcome.FromException<T>(new OperationCanceledException(context.CancellationToken));

                    if (delay.IsCompletedSuccessfully)
                        await StartAsync().ConfigureAwait(false);
                    continue;
                }

                int index = running.FindIndex(r => r.Task == completed);
                (Task<Outcome<T>> task, CancellationTokenSource cancellation) = running[index];
                running.RemoveAt(index);
                cancellation.Dispose();

                Outcome<T> outcome = await task.ConfigureAwait(false);
                if (!options.ShouldHandle(outcome))
                    return outcome;

                if (last is { } previous)
                    (previous.Result as IDisposable)?.Dispose();
                last = outcome;

                // Hata geldi: beklemeden yeni deneme (hak varsa).
                if (started <= options.MaxHedgedAttempts)
                    await StartAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await delayCancellation.CancelAsync().ConfigureAwait(false);

            // Kaybedenleri iptal et; tamamlandıklarında sonuçlarını bırak.
            foreach ((Task<Outcome<T>> task, CancellationTokenSource cancellation) in running)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                _ = task.ContinueWith(
                    t =>
                    {
                        if (t.IsCompletedSuccessfully)
                            (t.Result.Result as IDisposable)?.Dispose();
                        cancellation.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
            }
        }

        async ValueTask StartAsync()
        {
            int attempt = started++;
            if (attempt > 0 && options.OnHedging is not null)
                await options.OnHedging(new OnHedgingArguments(context, attempt)).ConfigureAwait(false);

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var attemptContext = new ResilienceContext(cancellation.Token, context.OperationKey) { AttemptNumber = attempt };
            foreach (KeyValuePair<string, object?> property in context.Properties)
                attemptContext.Properties[property.Key] = property.Value;

            Func<ValueTask<Outcome<T>>> action = attempt > 0 && options.ActionGenerator is not null
                ? options.ActionGenerator(new HedgingActionArguments<T>(attemptContext, attempt, callback))
                : () => InvokeAsync(callback, attemptContext);

            running.Add((RunAsync(action), cancellation));
        }

        static async Task<Outcome<T>> RunAsync(Func<ValueTask<Outcome<T>>> action)
        {
            await Task.Yield(); // denemeler gerçekten paralel başlasın
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return Outcome.FromException<T>(exception);
            }
        }
    }
}
