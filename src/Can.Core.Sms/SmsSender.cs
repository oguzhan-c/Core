using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Can.Core.Sms;

/// <summary>Uygulamanın kullandığı SMS göndericisi.</summary>
/// <example>
/// <code>
/// SmsSendResult result = await sms.SendAsync(new SmsMessage(user.Phone, $"Doğrulama kodun: {code}"), ct);
/// if (!result.Succeeded) return Error.Failure(result.ErrorCode!, "SMS gönderilemedi.");
/// </code>
/// </example>
public interface ISmsSender
{
    /// <summary>
    /// Numarayı normalleştirir, metni doğrular ve sağlayıcıya iletir. Geçersiz numara, boş/uzun metin ya da sağlayıcının
    /// reddi başarısız sonuç olarak döner; ağ hataları <see cref="SmsException"/> olarak atılır.
    /// </summary>
    Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sağlayıcı (Netgsm, İleti Merkezi, Twilio, Vonage ...): doğrulanmış isteği iletir. Uygulama bunu değil
/// <see cref="ISmsSender"/>'ı kullanır. HTTP API'li sağlayıcılar için <see cref="HttpSmsProvider"/>'dan türet.
/// </summary>
public interface ISmsProvider
{
    /// <summary>Loglarda ve ölçümlerde görünen ad.</summary>
    string Name { get; }

    Task<SmsSendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken = default);
}

public sealed class SmsOptions
{
    /// <summary>Numarada ülke kodu yoksa eklenecek kod.</summary>
    public string DefaultCountryCode { get; set; } = "90";

    /// <summary>Gönderici adı (sağlayıcıda onaylı başlık).</summary>
    public string? DefaultSender { get; set; }

    /// <summary>
    /// Türkçe karakterleri GSM karşılıklarına çevir (<c>ğ → g</c>): mesaj 70 yerine 160 karaktere sığar, maliyet düşer.
    /// Varsayılan kapalı (metin olduğu gibi gider).
    /// </summary>
    public bool TransliterateToGsm { get; set; }

    /// <summary>Bir mesajın en fazla kaç SMS olabileceği (yanlışlıkla uzun metin göndermeye karşı).</summary>
    public int MaxSegments { get; set; } = 6;
}

/// <summary>Ağ ya da sağlayıcı kaynaklı geçici hata (tekrar denenebilir).</summary>
public sealed class SmsException : Exception
{
    public SmsException() { }

    public SmsException(string message)
        : base(message) { }

    public SmsException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>Doğrulama + sağlayıcı + ölçüm.</summary>
internal sealed class SmsSender : ISmsSender
{
    private readonly ISmsProvider _provider;
    private readonly SmsOptions _options;

    public SmsSender(ISmsProvider provider, SmsOptions options)
    {
        _provider = provider;
        _options = options;
    }

    public async Task<SmsSendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!PhoneNumbers.TryNormalize(message.To, _options.DefaultCountryCode, out string to))
            return SmsSendResult.Failed(SmsErrorCodes.InvalidNumber, "Telefon numarası geçersiz.");

        string text = (message.Text ?? string.Empty).Trim();
        if (text.Length == 0)
            return SmsSendResult.Failed(SmsErrorCodes.EmptyText, "Mesaj boş olamaz.");

        if (_options.TransliterateToGsm)
            text = SmsText.ToGsm(text);

        SmsTextInfo info = SmsText.Analyze(text);
        if (info.Segments > _options.MaxSegments)
            return SmsSendResult.Failed(SmsErrorCodes.TooLong, $"Mesaj {info.Segments} SMS ediyor; en fazla {_options.MaxSegments}.", info.Segments);

        var request = new SmsRequest(to, text, message.From ?? _options.DefaultSender, message.Reference, message.IsCommercial, info.Encoding, info.Segments);

        using Activity? activity = SmsTelemetry.Source.StartActivity($"sms send {_provider.Name}", ActivityKind.Client);
        activity?.SetTag("can.sms.provider", _provider.Name);
        activity?.SetTag("can.sms.segments", info.Segments);
        activity?.SetTag("can.sms.encoding", info.Encoding.ToString());

        try
        {
            SmsSendResult result = await _provider.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                activity?.SetStatus(ActivityStatusCode.Error, result.ErrorCode);
            SmsTelemetry.Record(_provider.Name, result.Succeeded ? "sent" : "rejected", info.Segments);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            SmsTelemetry.Record(_provider.Name, "failed", info.Segments);
            throw;
        }
    }
}

/// <summary>SMS span'ları ve sayaçları. Numara ve metin etiketlenmez (kişisel veri, doğrulama kodu).</summary>
public static class SmsTelemetry
{
    public const string Name = "Can.Core.Sms";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>Etiketler: <c>can.sms.provider</c>, <c>can.outcome</c> (sent / rejected / failed).</summary>
    public static readonly Counter<long> Messages =
        Meter.CreateCounter<long>("can.sms.messages", unit: "{message}", description: "Gönderilen, reddedilen ya da hata alan SMS'ler");

    /// <summary>Faturalanan SMS parçaları (maliyet takibi).</summary>
    public static readonly Counter<long> Segments =
        Meter.CreateCounter<long>("can.sms.segments", unit: "{segment}", description: "Gönderilen SMS parçaları");

    internal static void Record(string provider, string outcome, int segments)
    {
        var providerTag = new KeyValuePair<string, object?>("can.sms.provider", provider);
        Messages.Add(1, providerTag, new KeyValuePair<string, object?>("can.outcome", outcome));
        if (outcome == "sent")
            Segments.Add(segments, providerTag);
    }
}
