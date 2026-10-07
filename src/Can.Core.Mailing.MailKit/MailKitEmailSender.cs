using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Can.Core.Mailing.MailKit;

public enum SmtpSecurity
{
    /// <summary>Sunucunun desteklediği en güvenli yöntem (587 → STARTTLS, 465 → SSL).</summary>
    Auto,

    /// <summary>Düz bağlantı + STARTTLS zorunlu (genelde 587).</summary>
    StartTls,

    /// <summary>Baştan SSL/TLS (genelde 465).</summary>
    SslOnConnect,

    /// <summary>Şifresiz; yalnızca yerel test sunucuları (MailHog, Mailpit, smtp4dev) için.</summary>
    None,
}

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    /// <summary>Boşsa kimlik doğrulama yapılmaz.</summary>
    public string? UserName { get; set; }

    /// <summary>Koda/appsettings'e yazma; User Secrets, ortam değişkeni ya da secret store kullan.</summary>
    public string? Password { get; set; }

    public string FromAddress { get; set; } = string.Empty;

    public string? FromName { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sunucu sertifikasının iptal (CRL/OCSP) kontrolü. Varsayılan açık; macOS gibi iptal listesine
    /// ulaşamayan ortamlarda "incomplete certificate revocation check" hatası alırsan kapat.
    /// Sertifika zinciri ve host adı doğrulaması kapatılsa da yapılmaya devam eder.
    /// </summary>
    public bool CheckCertificateRevocation { get; set; } = true;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException("SmtpOptions.Host boş olamaz.");

        if (string.IsNullOrWhiteSpace(FromAddress))
            throw new InvalidOperationException("SmtpOptions.FromAddress boş olamaz.");
    }
}

/// <summary>SMTP ile gönderen <see cref="IEmailSender"/> (MailKit).</summary>
public sealed class MailKitEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;

    public MailKitEmailSender(SmtpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        MailingTelemetry.InstrumentAsync("smtp", message, () => SendCoreAsync(message, cancellationToken));

    private async Task SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using MimeMessage mime = MimeMessageFactory.Create(message, new EmailAddress(_options.FromAddress, _options.FromName));
        using var client = new SmtpClient
        {
            Timeout = (int)_options.Timeout.TotalMilliseconds,
            CheckCertificateRevocation = _options.CheckCertificateRevocation,
        };

        SecureSocketOptions socketOptions = _options.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };

        await client.ConnectAsync(_options.Host, _options.Port, socketOptions, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_options.UserName))
            await client.AuthenticateAsync(_options.UserName, _options.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);

        await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Geliştirme ortamı için: e-postaları göndermek yerine klasöre <c>.eml</c> dosyası olarak yazar
/// (Outlook, Thunderbird ya da tarayıcıyla açılabilir).
/// </summary>
public sealed class PickupDirectoryEmailSender : IEmailSender
{
    private readonly string _directory;
    private readonly EmailAddress _defaultFrom;

    public PickupDirectoryEmailSender(string directory, EmailAddress defaultFrom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(defaultFrom);

        _directory = directory;
        _defaultFrom = defaultFrom;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        MailingTelemetry.InstrumentAsync("pickup", message, () => SendCoreAsync(message, cancellationToken));

    private async Task SendCoreAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        using MimeMessage mime = MimeMessageFactory.Create(message, _defaultFrom);

        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml");

        await mime.WriteToAsync(path, cancellationToken).ConfigureAwait(false);
    }
}

internal static class MimeMessageFactory
{
    public static MimeMessage Create(EmailMessage message, EmailAddress defaultFrom)
    {
        message.Validate();

        var mime = new MimeMessage();
        mime.From.Add(ToMailbox(message.From ?? defaultFrom));
        mime.To.AddRange(message.To.Select(ToMailbox));
        mime.Cc.AddRange(message.Cc.Select(ToMailbox));
        mime.Bcc.AddRange(message.Bcc.Select(ToMailbox));
        mime.ReplyTo.AddRange(message.ReplyTo.Select(ToMailbox));
        mime.Subject = message.Subject;

        var body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        };

        foreach (EmailAttachment attachment in message.Attachments)
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));

        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static MailboxAddress ToMailbox(EmailAddress address) => new(address.DisplayName ?? string.Empty, address.Address);
}
