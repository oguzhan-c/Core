using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Can.Core.Mailing.SendGrid;

public sealed class SendGridOptions
{
    /// <summary>SendGrid API anahtarı ("Mail Send" yetkisi yeterli). Koda/appsettings'e yazma; secret store kullan.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gönderen adres; SendGrid'de doğrulanmış (Sender Authentication) olmalı.</summary>
    public string FromAddress { get; set; } = string.Empty;

    public string? FromName { get; set; }

    /// <summary>
    /// Açıksa SendGrid isteği doğrular ama e-postayı göndermez (test/geliştirme). Kota harcanmaz.
    /// </summary>
    public bool SandboxMode { get; set; }

    /// <summary>API adresi. Yalnızca testlerde ya da AB veri yerleşimi (<c>https://api.eu.sendgrid.com/</c>) için değiştir.</summary>
    public Uri BaseAddress { get; set; } = new("https://api.sendgrid.com/");

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("SendGridOptions.ApiKey boş olamaz.");

        if (string.IsNullOrWhiteSpace(FromAddress))
            throw new InvalidOperationException("SendGridOptions.FromAddress boş olamaz.");
    }
}

/// <summary>SendGrid isteği başarısız olduğunda (yanlış anahtar, doğrulanmamış gönderen, kota ...).</summary>
public sealed class SendGridException : Exception
{
    public SendGridException(HttpStatusCode statusCode, string responseBody)
        : base($"SendGrid e-postayı kabul etmedi ({(int)statusCode} {statusCode}): {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string ResponseBody { get; }
}

/// <summary>SendGrid Web API v3 (<c>POST /v3/mail/send</c>) ile gönderir.</summary>
public sealed class SendGridEmailSender : IEmailSender
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly SendGridOptions _options;

    public SendGridEmailSender(HttpClient http, SendGridOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _http = http;
        _options = options;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        MailingTelemetry.InstrumentAsync("sendgrid", message, () => SendCoreAsync(message, cancellationToken));

    private async Task SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Validate();

        if (message.To.Count == 0)
            throw new ArgumentException("SendGrid en az bir 'To' alıcısı ister (yalnızca Cc/Bcc ile gönderilemez).");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseAddress, "v3/mail/send"))
        {
            Content = JsonContent.Create(CreatePayload(message), options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Başarı: 202 Accepted (sandbox modunda 200).
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new SendGridException(response.StatusCode, body);
        }
    }

    internal SendGridMail CreatePayload(EmailMessage message)
    {
        var content = new List<SendGridContent>();

        // SendGrid sırası: önce düz metin, sonra HTML.
        if (!string.IsNullOrWhiteSpace(message.TextBody))
            content.Add(new SendGridContent("text/plain", message.TextBody));

        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
            content.Add(new SendGridContent("text/html", message.HtmlBody));

        return new SendGridMail(
            Personalizations:
            [
                new SendGridPersonalization(
                    message.To.Select(ToAddress).ToList(),
                    message.Cc.Count > 0 ? message.Cc.Select(ToAddress).ToList() : null,
                    message.Bcc.Count > 0 ? message.Bcc.Select(ToAddress).ToList() : null
                ),
            ],
            From: ToAddress(message.From ?? new EmailAddress(_options.FromAddress, _options.FromName)),
            ReplyToList: message.ReplyTo.Count > 0 ? message.ReplyTo.Select(ToAddress).ToList() : null,
            Subject: message.Subject,
            Content: content,
            Attachments: message.Attachments.Count > 0
                ? message.Attachments.Select(a => new SendGridAttachment(Convert.ToBase64String(a.Content), a.FileName, a.ContentType, "attachment")).ToList()
                : null,
            MailSettings: _options.SandboxMode ? new SendGridMailSettings(new SendGridSetting(true)) : null
        );
    }

    private static SendGridAddress ToAddress(EmailAddress address) =>
        new(address.Address, string.IsNullOrWhiteSpace(address.DisplayName) ? null : address.DisplayName);
}

// SendGrid v3 istek gövdesi (snake_case).
internal sealed record SendGridMail(
    IReadOnlyList<SendGridPersonalization> Personalizations,
    SendGridAddress From,
    IReadOnlyList<SendGridAddress>? ReplyToList,
    string Subject,
    IReadOnlyList<SendGridContent> Content,
    IReadOnlyList<SendGridAttachment>? Attachments,
    SendGridMailSettings? MailSettings);

internal sealed record SendGridPersonalization(
    IReadOnlyList<SendGridAddress> To,
    IReadOnlyList<SendGridAddress>? Cc,
    IReadOnlyList<SendGridAddress>? Bcc);

internal sealed record SendGridAddress(string Email, string? Name);

internal sealed record SendGridContent(string Type, string Value);

internal sealed record SendGridAttachment(string Content, string Filename, string Type, string Disposition);

internal sealed record SendGridMailSettings(SendGridSetting SandboxMode);

internal sealed record SendGridSetting(bool Enable);
