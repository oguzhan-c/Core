using System.Security.Cryptography;
using System.Text;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace Can.Core.EventBus.AzureServiceBus;

/// <summary>Tek <see cref="ServiceBusClient"/> (bağlantıyı paylaşır, thread-safe) ve topic gönderici.</summary>
public sealed class AzureServiceBusInfrastructure : IAsyncDisposable
{
    private readonly AzureServiceBusOptions _options;
    private readonly Lazy<ServiceBusClient> _client;
    private readonly Lazy<ServiceBusSender> _sender;

    public AzureServiceBusInfrastructure(AzureServiceBusOptions options)
    {
        _options = options;
        _client = new Lazy<ServiceBusClient>(() => options.ConnectionString is { Length: > 0 } connection
            ? new ServiceBusClient(connection)
            : new ServiceBusClient(options.FullyQualifiedNamespace, options.Credential));
        _sender = new Lazy<ServiceBusSender>(() => _client.Value.CreateSender(options.TopicName));
    }

    public ServiceBusClient Client => _client.Value;

    public ServiceBusSender Sender => _sender.Value;

    public ServiceBusAdministrationClient CreateAdministrationClient() =>
        _options.ConnectionString is { Length: > 0 } connection
            ? new ServiceBusAdministrationClient(connection)
            : new ServiceBusAdministrationClient(_options.FullyQualifiedNamespace, _options.Credential);

    internal static ServiceBusMessage ToMessage(Guid eventId, string subject, string messageId, IReadOnlyDictionary<string, string> headers, string payload)
    {
        var message = new ServiceBusMessage(BinaryData.FromString(payload))
        {
            MessageId = messageId,
            Subject = subject,
            ContentType = "application/json",
            CorrelationId = eventId.ToString("D"),
        };
        foreach ((string key, string value) in headers)
            message.ApplicationProperties[key] = value;
        return message;
    }

    internal static Dictionary<string, string> ReadHeaders(ServiceBusReceivedMessage message)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, object value) in message.ApplicationProperties)
        {
            if (value is not null)
                headers[key] = value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (!headers.ContainsKey(EventHeaders.EventId) && Guid.TryParse(message.CorrelationId ?? message.MessageId, out Guid id))
            headers[EventHeaders.EventId] = id.ToString("D");
        if (!headers.ContainsKey(EventHeaders.EventName) && message.Subject is { Length: > 0 } subject && subject != AzureServiceBusOptions.RetrySubject)
            headers[EventHeaders.EventName] = subject;
        return headers;
    }

    /// <summary>Kural adı: event adından kısa ve sabit (kural adları en çok 50 karakter).</summary>
    internal static string RuleName(string eventName)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(eventName));
        return "e-" + Convert.ToHexStringLower(hash.AsSpan(0, 10));
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender.IsValueCreated)
            await _sender.Value.DisposeAsync().ConfigureAwait(false);
        if (_client.IsValueCreated)
            await _client.Value.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Event'i topic'e gönderir (<c>Subject</c> = event adı, <c>MessageId</c> = event kimliği: yinelenen tespiti açıksa çift gönderim elenir).</summary>
public sealed class AzureServiceBusEventTransport : IEventTransport
{
    private readonly AzureServiceBusInfrastructure _bus;

    public AzureServiceBusEventTransport(AzureServiceBusInfrastructure bus) => _bus = bus;

    public Task SendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ServiceBusMessage message = AzureServiceBusInfrastructure.ToMessage(
            envelope.EventId, envelope.EventName, envelope.EventId.ToString("D"), EventHeaders.From(envelope), envelope.Payload);
        return _bus.Sender.SendMessageAsync(message, cancellationToken);
    }
}
