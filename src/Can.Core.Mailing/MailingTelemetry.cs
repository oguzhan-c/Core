using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Can.Core.Mailing;

/// <summary>E-posta gönderimi için span ve sayaç. Adres/konu gibi kişisel veriler etiketlenmez.</summary>
public static class MailingTelemetry
{
    public const string Name = "Can.Core.Mailing";

    public static readonly ActivitySource Source = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>Gönderilen e-postalar. Etiketler: <c>can.email.provider</c>, <c>can.outcome</c> (sent / failed).</summary>
    public static readonly Counter<long> Emails =
        Meter.CreateCounter<long>("can.email.messages", unit: "{message}", description: "Gönderilen ya da hata alan e-postalar");

    /// <summary>Gönderimi ölçerek çalıştırır (sağlayıcılar kullanır).</summary>
    public static async Task InstrumentAsync(string provider, EmailMessage message, Func<Task> send)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(send);

        using Activity? activity = Source.StartActivity($"email send {provider}", ActivityKind.Client);
        activity?.SetTag("can.email.provider", provider);
        activity?.SetTag("can.email.recipients", message.To.Count + message.Cc.Count + message.Bcc.Count);
        activity?.SetTag("can.email.attachments", message.Attachments.Count);

        try
        {
            await send().ConfigureAwait(false);
            Emails.Add(1, new KeyValuePair<string, object?>("can.email.provider", provider), new KeyValuePair<string, object?>("can.outcome", "sent"));
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            Emails.Add(1, new KeyValuePair<string, object?>("can.email.provider", provider), new KeyValuePair<string, object?>("can.outcome", "failed"));
            throw;
        }
    }
}
