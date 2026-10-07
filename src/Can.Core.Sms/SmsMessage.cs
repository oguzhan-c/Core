namespace Can.Core.Sms;

/// <summary>Gönderilecek SMS.</summary>
/// <param name="To">Alıcı numarası; serbest biçimde verilebilir (<c>0532 123 45 67</c>), gönderimden önce E.164'e çevrilir.</param>
/// <param name="Text">Mesaj metni.</param>
public sealed record SmsMessage(string To, string Text)
{
    /// <summary>Gönderici adı (başlık / originator). Boşsa <see cref="SmsOptions.DefaultSender"/>.</summary>
    public string? From { get; init; }

    /// <summary>
    /// Uygulamanın kendi kimliği (ör. doğrulama kodu kaydının Id'si). Sağlayıcı destekliyorsa tekrar gönderimi önlemek ve
    /// teslim raporunu eşleştirmek için iletilir.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>Ticari ileti mi (İYS onayı gerekir). Doğrulama kodu gibi bilgilendirme mesajlarında <see langword="false"/>.</summary>
    public bool IsCommercial { get; init; }
}

/// <summary>Sağlayıcıya giden, doğrulanmış ve normalleştirilmiş istek.</summary>
/// <param name="To">E.164 numara (<c>+905321234567</c>).</param>
/// <param name="Text">Gönderilecek metin (gerekirse GSM karakterlerine çevrilmiş).</param>
/// <param name="From">Gönderici adı.</param>
/// <param name="Reference">Uygulamanın kimliği.</param>
/// <param name="IsCommercial">Ticari ileti.</param>
/// <param name="Encoding">Metnin kodlaması.</param>
/// <param name="Segments">Kaç SMS olarak faturalanacağı.</param>
public sealed record SmsRequest(string To, string Text, string? From, string? Reference, bool IsCommercial, SmsEncoding Encoding, int Segments);

/// <summary>Gönderim sonucu. Sağlayıcının reddettiği istekler (geçersiz numara, yetersiz kredi ...) hata olarak döner.</summary>
public sealed record SmsSendResult
{
    private SmsSendResult(bool succeeded, string? messageId, string? errorCode, string? errorMessage, int segments)
    {
        Succeeded = succeeded;
        MessageId = messageId;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Segments = segments;
    }

    public bool Succeeded { get; }

    /// <summary>Sağlayıcının mesaj kimliği (teslim raporu sorgulamak için).</summary>
    public string? MessageId { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public int Segments { get; }

    public static SmsSendResult Sent(string? messageId, int segments) => new(true, messageId, null, null, segments);

    public static SmsSendResult Failed(string errorCode, string errorMessage, int segments = 0) => new(false, null, errorCode, errorMessage, segments);
}

/// <summary>Hata kodları (sağlayıcılar kendi kodlarını da dönebilir).</summary>
public static class SmsErrorCodes
{
    public const string InvalidNumber = "sms.invalid_number";
    public const string EmptyText = "sms.empty_text";
    public const string TooLong = "sms.too_long";
    public const string Rejected = "sms.rejected";
    public const string Unavailable = "sms.unavailable";
}
