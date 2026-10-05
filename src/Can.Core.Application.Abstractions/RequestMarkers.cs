namespace Can.Core.Application;

// Command/query'lerin hangi pipeline davranışlarına gireceğini belirleyen işaretleyiciler
// (nArchitecture'daki gibi). Bir istek birden fazlasını uygulayabilir:
//
//   public sealed record DeleteProductCommand(int Id)
//       : IRequest, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
//   {
//       public IReadOnlyCollection<string> Roles => ["Admin", "Product.Write"];
//       public IReadOnlyCollection<string> CacheTagsToRemove => ["products"];
//   }

/// <summary>
/// Yalnızca giriş yapmış kullanıcılar çalıştırabilir. <see cref="Roles"/> doluysa kullanıcının bu
/// rollerden en az birine sahip olması gerekir (yönetici rolü her zaman geçer).
/// </summary>
public interface ISecuredRequest
{
    IReadOnlyCollection<string> Roles => [];
}

/// <summary>Handler bir transaction içinde çalışır; başarılıysa kaydedilip commit edilir, hata olursa geri alınır.</summary>
public interface ITransactionalRequest { }

/// <summary>Yanıt önbelleğe alınır (yalnızca query'ler için kullan).</summary>
public interface ICachableRequest
{
    /// <summary>Önbellek anahtarı; isteğin parametrelerini içermeli: <c>$"products:{PageIndex}:{PageSize}"</c>.</summary>
    string CacheKey { get; }

    /// <summary>Toplu silme için etiketler (ör. <c>["products"]</c>).</summary>
    IReadOnlyCollection<string> CacheTags => [];

    /// <summary>Boşsa uygulama varsayılanı kullanılır.</summary>
    TimeSpan? CacheExpiration => null;

    /// <summary><see langword="true"/> ise önbellek atlanır ve handler her zaman çalışır.</summary>
    bool BypassCache => false;
}

/// <summary>Handler başarıyla bittikten sonra ilgili önbellek kayıtları silinir (command'lar için).</summary>
public interface ICacheRemoverRequest
{
    IReadOnlyCollection<string> CacheTagsToRemove => [];

    IReadOnlyCollection<string> CacheKeysToRemove => [];
}

/// <summary>
/// İstek başlangıcı ve bitişi, kullanıcı bilgisiyle birlikte loglanır.
/// İstek gövdesi loglanmaz (parola gibi hassas veriler sızmasın diye).
/// </summary>
public interface ILoggableRequest { }
