namespace Can.Core.Application;

/// <summary>
/// Bir HTTP isteğinin kaydı: kim, ne zaman, hangi tenant'ta, hangi uç noktayı çağırdı, sonuç ne oldu, ne kadar sürdü,
/// nereden geldi. İstek ve yanıt gövdesi HİÇBİR zaman kaydedilmez; sorgu metnindeki gizli değerler maskelenir.
/// </summary>
/// <param name="Timestamp">İsteğin başladığı an.</param>
/// <param name="Method">HTTP yöntemi.</param>
/// <param name="Path">İstek yolu (<c>/api/v2/orders/42</c>).</param>
/// <param name="Route">Uç noktanın yol şablonu (<c>/api/v2/orders/{id}</c>); eşleşmediyse <see langword="null"/>.</param>
/// <param name="Query">Sorgu metni, gizli değerler maskeli (<c>?page=2&amp;token=***</c>).</param>
/// <param name="StatusCode">Yanıt kodu (işlenmemiş hatada 500).</param>
/// <param name="DurationMs">Süre (milisaniye).</param>
/// <param name="UserId">Giriş yapmış kullanıcı.</param>
/// <param name="TenantId">Aktif tenant.</param>
/// <param name="SessionId">Oturum (<c>sid</c>).</param>
/// <param name="IpAddress">İstemcinin IP'si.</param>
/// <param name="UserAgent">Tarayıcı/uygulama bilgisi.</param>
/// <param name="TraceId">Dağıtık izleme kimliği (Sentry gibi ön yüz araçlarıyla ve loglarla eşleştirmek için).</param>
public sealed record RequestActivity(
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    string? Route,
    string? Query,
    int StatusCode,
    double DurationMs,
    string? UserId,
    string? TenantId,
    string? SessionId,
    string? IpAddress,
    string? UserAgent,
    string? TraceId);

/// <summary>
/// İstek kayıtlarını bir yere yazar (veritabanı, Elasticsearch, log, SIEM ...). Kayıtlar arka planda partiler halinde
/// gelir; yazma isteği hiç bekletmez. Birden çok sink kaydedilebilir; hata fırlatırsa loglanır, diğerleri devam eder.
/// </summary>
public interface IRequestActivitySink
{
    ValueTask WriteAsync(IReadOnlyList<RequestActivity> batch, CancellationToken cancellationToken = default);
}
