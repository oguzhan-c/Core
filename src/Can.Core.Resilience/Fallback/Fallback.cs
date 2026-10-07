namespace Can.Core.Resilience;

public readonly record struct FallbackActionArguments<T>(Outcome<T> Outcome, ResilienceContext Context);

public readonly record struct OnFallbackArguments<T>(Outcome<T> Outcome, ResilienceContext Context);

/// <summary>Hata durumunda yedek sonuç verir (ör. önbellekteki son değer, varsayılan liste).</summary>
public sealed class FallbackOptions<T>
{
    public Func<Outcome<T>, bool> ShouldHandle { get; set; } = DefaultPredicates.HandleExceptions;

    /// <summary>Yedek sonucu üretir (zorunlu). <c>args =&gt; ValueTask.FromResult(Outcome.FromResult(varsayılan))</c>.</summary>
    public Func<FallbackActionArguments<T>, ValueTask<Outcome<T>>>? FallbackAction { get; set; }

    public Func<OnFallbackArguments<T>, ValueTask>? OnFallback { get; set; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ShouldHandle);
        if (FallbackAction is null)
            throw new ArgumentException("FallbackOptions.FallbackAction zorunlu.", nameof(FallbackAction));
    }
}

internal sealed class FallbackStrategy<T>(FallbackOptions<T> options) : ResilienceStrategy<T>
{
    protected internal override async ValueTask<Outcome<T>> ExecuteCoreAsync(Func<ResilienceContext, ValueTask<Outcome<T>>> callback, ResilienceContext context)
    {
        Outcome<T> outcome = await InvokeAsync(callback, context).ConfigureAwait(false);
        if (!options.ShouldHandle(outcome))
            return outcome;

        if (options.OnFallback is not null)
            await options.OnFallback(new OnFallbackArguments<T>(outcome, context)).ConfigureAwait(false);

        try
        {
            return await options.FallbackAction!(new FallbackActionArguments<T>(outcome, context)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return Outcome.FromException<T>(exception);
        }
    }
}
