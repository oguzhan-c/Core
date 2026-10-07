using System.Net;

namespace Can.Core.Sms;

/// <summary>
/// HTTP API'li sağlayıcılar için temel sınıf: isteği kur, yanıtı yorumla; bağlantı hataları, 5xx ve 429
/// <see cref="SmsException"/> olur (tekrar denenebilir), diğer başarısız yanıtlar <see cref="ReadResponseAsync"/>'e gelir.
/// </summary>
/// <example>
/// <code>
/// public sealed class AcmeSmsProvider(HttpClient http, AcmeOptions options) : HttpSmsProvider(http)
/// {
///     public override string Name =&gt; "acme";
///
///     protected override HttpRequestMessage CreateRequest(SmsRequest sms) =&gt;
///         new(HttpMethod.Post, "https://api.acme.example/v1/sms")
///         {
///             Content = JsonContent.Create(new { to = sms.To, text = sms.Text, sender = sms.From, apiKey = options.ApiKey }),
///         };
///
///     protected override async Task&lt;SmsSendResult&gt; ReadResponseAsync(HttpResponseMessage response, SmsRequest sms, CancellationToken ct)
///     {
///         var body = await response.Content.ReadFromJsonAsync&lt;AcmeResponse&gt;(ct);
///         return body?.Status == "ok"
///             ? SmsSendResult.Sent(body.MessageId, sms.Segments)
///             : SmsSendResult.Failed(SmsErrorCodes.Rejected, body?.Error ?? "Reddedildi.");
///     }
/// }
///
/// builder.Services.AddCanSms(o =&gt; o.DefaultSender = "ACME")
///     .UseHttpProvider&lt;AcmeSmsProvider&gt;();          // HttpClient + dayanıklılık eklenebilir
/// </code>
/// </example>
public abstract class HttpSmsProvider : ISmsProvider
{
    protected HttpSmsProvider(HttpClient http)
    {
        Http = http ?? throw new ArgumentNullException(nameof(http));
    }

    protected HttpClient Http { get; }

    public abstract string Name { get; }

    public async Task<SmsSendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using HttpRequestMessage message = CreateRequest(request);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new SmsException($"{Name} SMS servisine ulaşılamadı: {ex.Message}", ex);
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new SmsException($"{Name} SMS servisi geçici olarak yanıt vermiyor ({(int)response.StatusCode}).");

            return await ReadResponseAsync(response, request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sağlayıcının isteği (adres, kimlik doğrulama, gövde).</summary>
    protected abstract HttpRequestMessage CreateRequest(SmsRequest request);

    /// <summary>Yanıtı sonuca çevirir (2xx ve 4xx yanıtlar; birçok sağlayıcı hatayı 200 içinde döner).</summary>
    protected abstract Task<SmsSendResult> ReadResponseAsync(HttpResponseMessage response, SmsRequest request, CancellationToken cancellationToken);
}
