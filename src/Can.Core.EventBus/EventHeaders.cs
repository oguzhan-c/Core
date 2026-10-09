using System.Globalization;

namespace Can.Core.EventBus;

/// <summary>
/// Zarfın broker mesajındaki başlıkları. Gövde (body) event'in JSON'udur; kimlik, ad, tenant, zaman ve deneme sayısı
/// başlıklarda taşınır. Böylece RabbitMQ, Kafka, Azure Service Bus ve kendi taşıyıcın aynı biçimi kullanır.
/// </summary>
public static class EventHeaders
{
    public const string EventId = "can-event-id";
    public const string EventName = "can-event-name";
    public const string TenantId = "can-tenant-id";
    public const string OccurredAt = "can-occurred-at";

    /// <summary>Kaçıncı deneme (1'den başlar; yeniden deneme kuyruğuna her gidişte artar).</summary>
    public const string Attempt = "can-attempt";

    /// <summary>Gecikmeli denemede mesajın işlenebileceği en erken an (Kafka gibi gecikme desteği olmayan broker'lar için).</summary>
    public const string NotBefore = "can-not-before";

    /// <summary>Yeniden deneme / DLQ mesajının hangi tüketiciye ait olduğu.</summary>
    public const string Consumer = "can-consumer";

    public const string Error = "can-error";
    public const string ErrorType = "can-error-type";
    public const string FailedAt = "can-failed-at";

    private static readonly HashSet<string> System = new(StringComparer.OrdinalIgnoreCase)
    {
        EventId, EventName, TenantId, OccurredAt, Attempt, NotBefore, Consumer, Error, ErrorType, FailedAt,
    };

    /// <summary>Zarfın başlıkları (zarfın kendi başlıkları — traceparent vb. — dahil).</summary>
    public static Dictionary<string, string> From(EventEnvelope envelope, int attempt = 1)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in envelope.Headers)
        {
            if (!System.Contains(key))
                headers[key] = value;
        }

        headers[EventId] = envelope.EventId.ToString("D");
        headers[EventName] = envelope.EventName;
        headers[OccurredAt] = envelope.OccurredAt.ToString("O", CultureInfo.InvariantCulture);
        headers[Attempt] = attempt.ToString(CultureInfo.InvariantCulture);
        if (envelope.TenantId is not null)
            headers[TenantId] = envelope.TenantId;
        return headers;
    }

    /// <summary>Başlıklar ve gövdeden zarf. Zorunlu başlık eksikse <see cref="FormatException"/>.</summary>
    public static EventEnvelope ToEnvelope(IReadOnlyDictionary<string, string> headers, string payload)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(payload);

        if (!headers.TryGetValue(EventId, out string? id) || !Guid.TryParse(id, out Guid eventId))
            throw new FormatException($"'{EventId}' başlığı yok ya da geçersiz.");
        if (!headers.TryGetValue(EventName, out string? name) || string.IsNullOrWhiteSpace(name))
            throw new FormatException($"'{EventName}' başlığı yok.");

        DateTimeOffset occurredAt = headers.TryGetValue(OccurredAt, out string? at)
            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in headers)
        {
            if (!System.Contains(key))
                extra[key] = value;
        }

        return new EventEnvelope(eventId, name, payload, headers.GetValueOrDefault(TenantId), occurredAt) { Headers = extra };
    }

    /// <summary>Deneme sayısı (yoksa 1).</summary>
    public static int AttemptOf(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue(Attempt, out string? value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int attempt) && attempt > 0
            ? attempt
            : 1;

    /// <summary>Gecikmeli denemenin zamanı (yoksa <see langword="null"/>).</summary>
    public static DateTimeOffset? NotBeforeOf(IReadOnlyDictionary<string, string> headers) =>
        headers.TryGetValue(NotBefore, out string? value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at)
            ? at
            : null;

    /// <summary>Yeniden deneme mesajının başlıkları: deneme +1, en erken işlenme anı ve tüketici.</summary>
    public static Dictionary<string, string> ForRetry(IReadOnlyDictionary<string, string> headers, string consumer, int nextAttempt, DateTimeOffset notBefore)
    {
        var next = new Dictionary<string, string>(headers, StringComparer.Ordinal)
        {
            [Attempt] = nextAttempt.ToString(CultureInfo.InvariantCulture),
            [NotBefore] = notBefore.ToString("O", CultureInfo.InvariantCulture),
            [Consumer] = consumer,
        };
        return next;
    }

    /// <summary>DLQ mesajının başlıkları: hata, tipi ve zamanı (hata metni 1000 karakterle sınırlı).</summary>
    public static Dictionary<string, string> ForDeadLetter(IReadOnlyDictionary<string, string> headers, string consumer, string reason, Exception? error, DateTimeOffset failedAt)
    {
        string message = error?.Message ?? reason;
        var next = new Dictionary<string, string>(headers, StringComparer.Ordinal)
        {
            [Consumer] = consumer,
            [Error] = message.Length > 1000 ? message[..1000] : message,
            [ErrorType] = error?.GetType().FullName ?? reason,
            [FailedAt] = failedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        next.Remove(NotBefore);
        return next;
    }
}
