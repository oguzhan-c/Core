namespace Can.Core.Resilience;

public enum DelayBackoffType
{
    /// <summary>Her denemede aynı bekleme.</summary>
    Constant,

    /// <summary><c>Delay × (deneme + 1)</c>.</summary>
    Linear,

    /// <summary><c>Delay × 2^deneme</c>.</summary>
    Exponential,
}

public readonly record struct RetryDelayArguments<T>(Outcome<T> Outcome, ResilienceContext Context, int AttemptNumber);

public readonly record struct OnRetryArguments<T>(Outcome<T> Outcome, ResilienceContext Context, int AttemptNumber, TimeSpan RetryDelay);

/// <summary>Başarısız denemeyi beklemeyle tekrarlar.</summary>
public class RetryOptions<T>
{
    /// <summary>İlk denemeden sonra en fazla kaç kez tekrar denensin (sonsuz için <see cref="int.MaxValue"/>).</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Temel bekleme (<see cref="BackoffType"/> ile büyür).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Beklemenin üst sınırı (üstel büyümede gereklidir).</summary>
    public TimeSpan? MaxDelay { get; set; }

    public DelayBackoffType BackoffType { get; set; } = DelayBackoffType.Constant;

    /// <summary>
    /// Beklemeye ±%25 rastgelelik ekler: aynı anda hata alan istemcilerin aynı anda tekrar denemesi (thundering herd) önlenir.
    /// </summary>
    public bool UseJitter { get; set; }

    /// <summary>Hangi sonuçlar tekrar denensin. Varsayılan: iptal dışındaki tüm exception'lar.</summary>
    public Func<Outcome<T>, bool> ShouldHandle { get; set; } = DefaultPredicates.HandleExceptions;

    /// <summary>
    /// Beklemeyi kendin hesapla (ör. sunucunun <c>Retry-After</c> değeri); <see langword="null"/> dönerse varsayılan hesap.
    /// </summary>
    public Func<RetryDelayArguments<T>, TimeSpan?>? DelayGenerator { get; set; }

    /// <summary>Her tekrar denemeden önce (log, metrik).</summary>
    public Func<OnRetryArguments<T>, ValueTask>? OnRetry { get; set; }

    /// <summary>Testler için rastgele sayı kaynağı ([0, 1)).</summary>
    public Func<double> Randomizer { get; set; } = Random.Shared.NextDouble;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetryAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(Delay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(ShouldHandle);
        ArgumentNullException.ThrowIfNull(Randomizer);
    }
}

/// <inheritdoc />
public sealed class RetryOptions : RetryOptions<object?>;

internal sealed class RetryStrategy<T>(RetryOptions<T> options, TimeProvider timeProvider) : ResilienceStrategy<T>
{
    private static readonly TimeSpan MaxSupportedDelay = TimeSpan.FromDays(1);

    protected internal override async ValueTask<Outcome<T>> ExecuteCoreAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        for (int attempt = 0; ; attempt++)
        {
            context.AttemptNumber = attempt;
            Outcome<T> outcome = await InvokeAsync(callback, context).ConfigureAwait(false);

            if (attempt >= options.MaxRetryAttempts || context.CancellationToken.IsCancellationRequested || !options.ShouldHandle(outcome))
                return outcome;

            TimeSpan delay = options.DelayGenerator?.Invoke(new RetryDelayArguments<T>(outcome, context, attempt)) ?? CalculateDelay(attempt);

            if (options.OnRetry is not null)
                await options.OnRetry(new OnRetryArguments<T>(outcome, context, attempt, delay)).ConfigureAwait(false);

            // Kullanılmayacak sonucu bırak (ör. HttpResponseMessage bağlantıyı tutmasın).
            (outcome.Result as IDisposable)?.Dispose();

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, timeProvider, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                {
                    return Outcome.FromException<T>(exception);
                }
            }
        }
    }

    internal TimeSpan CalculateDelay(int attempt)
    {
        double ticks = options.BackoffType switch
        {
            DelayBackoffType.Linear => options.Delay.Ticks * (attempt + 1.0),
            DelayBackoffType.Exponential => options.Delay.Ticks * Math.Pow(2, attempt),
            _ => options.Delay.Ticks,
        };

        if (options.UseJitter)
            ticks *= 0.75 + (options.Randomizer() * 0.5); // ±%25

        TimeSpan max = options.MaxDelay is { } m && m < MaxSupportedDelay ? m : MaxSupportedDelay;
        return ticks >= max.Ticks || double.IsInfinity(ticks) ? max : TimeSpan.FromTicks((long)ticks);
    }
}
