using System.Text;
using global::RabbitMQ.Client;

namespace Can.Core.EventBus.RabbitMQ;

/// <summary>Zarf ↔ AMQP mesajı.</summary>
internal static class RabbitMqMessages
{
    /// <summary>Mesaj özellikleri: kalıcı, JSON, kimlik ve tip; Can başlıkları AMQP başlıklarına (metin) yazılır.</summary>
    public static BasicProperties Properties(Guid eventId, string eventName, IReadOnlyDictionary<string, string> headers, DateTimeOffset occurredAt)
    {
        var amqpHeaders = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string key, string value) in headers)
            amqpHeaders[key] = value;

        return new BasicProperties
        {
            MessageId = eventId.ToString("D"),
            Type = eventName,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(occurredAt.ToUnixTimeSeconds()),
            Headers = amqpHeaders,
        };
    }

    /// <summary>
    /// Gelen başlıklar. AMQP metinleri istemciden <c>byte[]</c> olarak gelir (UTF-8 çözülür). RabbitMQ'nun kendi
    /// <c>x-</c> başlıkları (<c>x-death</c>, <c>x-delivery-count</c> ...) alınmaz: yeniden yayında taşınırsa broker
    /// döngü sanıp mesajı sessizce düşürür.
    /// </summary>
    public static Dictionary<string, string> ReadHeaders(IReadOnlyBasicProperties properties)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, object? value) in properties.Headers ?? new Dictionary<string, object?>())
        {
            if (key.StartsWith("x-", StringComparison.OrdinalIgnoreCase))
                continue;
            string? text = value switch
            {
                null => null,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string s => s,
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            };
            if (text is not null)
                headers[key] = text;
        }

        // Can dışı yayıncılar için yedek: kimlik ve ad mesaj özelliklerinden
        if (!headers.ContainsKey(EventHeaders.EventId) && properties.MessageId is { Length: > 0 } messageId)
            headers[EventHeaders.EventId] = messageId;
        if (!headers.ContainsKey(EventHeaders.EventName) && properties.Type is { Length: > 0 } type)
            headers[EventHeaders.EventName] = type;
        return headers;
    }
}
