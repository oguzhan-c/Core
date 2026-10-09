using System.Text;
using global::RabbitMQ.Client;

namespace Can.Core.EventBus.RabbitMQ;

/// <summary>Event'i topic exchange'e yayınlar (routing key = event adı) ve broker'ın kalıcı onayını bekler.</summary>
public sealed class RabbitMqEventTransport : IEventTransport
{
    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly RabbitMqTopology _topology;

    public RabbitMqEventTransport(RabbitMqConnection connection, RabbitMqOptions options, RabbitMqTopology topology)
    {
        _connection = connection;
        _options = options;
        _topology = topology;
    }

    public async Task SendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await _topology.EnsureExchangeAsync(cancellationToken).ConfigureAwait(false);

        IChannel channel = await _connection.GetPublisherAsync(cancellationToken).ConfigureAwait(false);
        BasicProperties properties = RabbitMqMessages.Properties(envelope.EventId, envelope.EventName, EventHeaders.From(envelope), envelope.OccurredAt);

        // mandatory: false — dinleyeni olmayan event normaldir (hiçbir kuyruğa yönlenmez, hata değildir)
        await channel.BasicPublishAsync(_options.Exchange, envelope.EventName, mandatory: false, properties, Encoding.UTF8.GetBytes(envelope.Payload), cancellationToken)
            .ConfigureAwait(false);
    }
}
