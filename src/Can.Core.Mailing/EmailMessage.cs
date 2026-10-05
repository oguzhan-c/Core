namespace Can.Core.Mailing;

public sealed record EmailAddress(string Address, string? DisplayName = null)
{
    public override string ToString() => DisplayName is null ? Address : $"{DisplayName} <{Address}>";
}

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType = "application/octet-stream");

/// <summary>Gönderilecek e-posta.</summary>
/// <example>
/// <code>
/// var message = new EmailMessage("Doğrulama kodun")
/// {
///     HtmlBody = $"&lt;p&gt;Kodun: &lt;b&gt;{code}&lt;/b&gt;&lt;/p&gt;",
///     TextBody = $"Kodun: {code}",
/// };
/// message.To.Add(new EmailAddress(user.Email));
/// await emailSender.SendAsync(message, ct);
/// </code>
/// </example>
public sealed class EmailMessage
{
    public EmailMessage(string subject)
    {
        Subject = subject;
    }

    /// <summary>Boşsa göndericinin varsayılan adresi kullanılır.</summary>
    public EmailAddress? From { get; set; }

    public List<EmailAddress> To { get; } = [];

    public List<EmailAddress> Cc { get; } = [];

    public List<EmailAddress> Bcc { get; } = [];

    public List<EmailAddress> ReplyTo { get; } = [];

    public string Subject { get; set; }

    public string? HtmlBody { get; set; }

    /// <summary>HTML gösteremeyen istemciler için düz metin.</summary>
    public string? TextBody { get; set; }

    public List<EmailAttachment> Attachments { get; } = [];

    /// <summary>Alıcı, konu ve gövde kontrolü; geçersizse <see cref="ArgumentException"/>.</summary>
    public void Validate()
    {
        if (To.Count + Cc.Count + Bcc.Count == 0)
            throw new ArgumentException("E-postanın en az bir alıcısı olmalı.");

        if (string.IsNullOrWhiteSpace(Subject))
            throw new ArgumentException("E-posta konusu boş olamaz.");

        if (string.IsNullOrWhiteSpace(HtmlBody) && string.IsNullOrWhiteSpace(TextBody))
            throw new ArgumentException("E-postanın HTML ya da düz metin gövdesi olmalı.");

        foreach (EmailAddress address in To.Concat(Cc).Concat(Bcc).Concat(ReplyTo))
        {
            if (string.IsNullOrWhiteSpace(address.Address) || !address.Address.Contains('@', StringComparison.Ordinal))
                throw new ArgumentException($"Geçersiz e-posta adresi: '{address.Address}'.");
        }
    }
}

/// <summary>
/// E-posta gönderir. Uygulama kodu yalnızca bu arayüzü kullanır; SMTP, geliştirme klasörü ya da test
/// göndericisi DI'da seçilir.
/// </summary>
/// <remarks>
/// İstek içinde doğrudan göndermek yerine (yavaş SMTP isteği yanıtı geciktirir, hata işlemi bozar) domain
/// event handler'ından ya da arka plan işinden göndermek önerilir.
/// </remarks>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
