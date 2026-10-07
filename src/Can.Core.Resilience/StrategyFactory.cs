namespace Can.Core.Resilience;

/// <summary>
/// Bir stratejiyi istenen sonuç tipi için üretir. Tipsiz pipeline (<see cref="ResiliencePipeline"/>) her sonuç tipi için
/// ayrı (tipli) bir zincir kurar; böylece sonuçlar <c>object</c>'e kutulanmaz. Durumlu stratejilerin (circuit breaker,
/// rate limiter) durumu fabrikada tutulur ve tüm tipler arasında paylaşılır.
/// </summary>
internal interface IStrategyFactory
{
    ResilienceStrategy<TResult> Create<TResult>();
}

/// <summary>Seçenekleri <typeparamref name="TOptions"/> tipinde olan strateji fabrikalarının tabanı.</summary>
internal abstract class StrategyFactory<TOptions> : IStrategyFactory
{
    public ResilienceStrategy<TResult> Create<TResult>() =>
        typeof(TResult) == typeof(TOptions) ? (ResilienceStrategy<TResult>)(object)CreateNative() : CreateAdapted<TResult>();

    /// <summary>Seçeneklerle aynı tip (dönüşüm yok).</summary>
    protected abstract ResilienceStrategy<TOptions> CreateNative();

    /// <summary>Tipsiz seçenekler başka bir sonuç tipiyle kullanılıyor: predicate'ler dönüştürülerek çağrılır.</summary>
    protected abstract ResilienceStrategy<TResult> CreateAdapted<TResult>();
}

/// <summary>Kullanıcının kendi stratejisi: yalnızca kendi tipi; tipsiz pipeline'da sonuç kutulanarak çalışır.</summary>
internal sealed class CustomStrategyFactory<T>(ResilienceStrategy<T> strategy) : StrategyFactory<T>
{
    protected override ResilienceStrategy<T> CreateNative() => strategy;

    protected override ResilienceStrategy<TResult> CreateAdapted<TResult>() => new BoxingAdapterStrategy<TResult, T>(strategy);
}

/// <summary><typeparamref name="TInner"/> tipli bir stratejiyi <typeparamref name="TResult"/> için çalıştırır (kutulayarak).</summary>
internal sealed class BoxingAdapterStrategy<TResult, TInner>(ResilienceStrategy<TInner> inner) : ResilienceStrategy<TResult>
{
    protected internal override async ValueTask<Outcome<TResult>> ExecuteCoreAsync(
        Func<ResilienceContext, ValueTask<Outcome<TResult>>> callback,
        ResilienceContext context)
    {
        Outcome<TInner> outcome = await inner.ExecuteCoreAsync(async c => (await callback(c).ConfigureAwait(false)).Cast<TInner>(), context)
            .ConfigureAwait(false);
        return outcome.Cast<TResult>();
    }
}
