namespace Can.Core.Mediator;

/// <summary>Zincirdeki bir sonraki adım (bir sonraki behavior ya da handler).</summary>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>
/// Handler'ın etrafına sarılan ara katman: validation, authorization, caching, logging,
/// transaction ... Kayıt sırası çalışma sırasıdır: ilk eklenen en dışta çalışır.
/// </summary>
/// <remarks>
/// Belirli isteklere uygulamak için generic kısıt kullan; kısıtı sağlamayan istekler için
/// behavior otomatik atlanır:
/// <code>
/// public sealed class CachingBehavior&lt;TRequest, TResponse&gt; : IPipelineBehavior&lt;TRequest, TResponse&gt;
///     where TRequest : ICachableRequest
/// </code>
/// </remarks>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
