using global::RabbitMQ.Client;

namespace Can.Core.EventBus.RabbitMQ;

/// <summary>Exchange, kuyruklar ve bağlamalar (idempotent: var olanı tekrar bildirmek bir şey değiştirmez).</summary>
public sealed class RabbitMqTopology
{
    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private volatile bool _exchangeReady;

    public RabbitMqTopology(RabbitMqConnection connection, RabbitMqOptions options)
    {
        _connection = connection;
        _options = options;
    }

    public async Task EnsureExchangeAsync(CancellationToken cancellationToken)
    {
        if (_exchangeReady || !_options.DeclareTopology)
            return;

        IConnection connection = await _connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        _exchangeReady = true;
    }

    /// <summary>
    /// Ana kuyruk (event adlarıyla exchange'e bağlı), her gecikme için bir bekleme kuyruğu (TTL dolunca varsayılan
    /// exchange üzerinden ana kuyruğa döner) ve DLQ. Ana kuyruğun DLX'i DLQ'dur: broker'ın teslim sınırı (quorum
    /// varsayılanı 20) aşılırsa mesaj kaybolmaz.
    /// </summary>
    public async Task EnsureConsumerAsync(IChannel channel, IReadOnlyList<string> events, CancellationToken cancellationToken)
    {
        if (!_options.DeclareTopology)
            return;

        await channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        _exchangeReady = true;

        await channel.QueueDeclareAsync(_options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, QueueArguments(), cancellationToken: cancellationToken).ConfigureAwait(false);

        Dictionary<string, object?> main = QueueArguments();
        main["x-dead-letter-exchange"] = string.Empty;
        main["x-dead-letter-routing-key"] = _options.DeadLetterQueue;
        await channel.QueueDeclareAsync(_options.Queue, durable: true, exclusive: false, autoDelete: false, main, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (TimeSpan delay in _options.DistinctDelays)
        {
            Dictionary<string, object?> retry = QueueArguments();
            retry["x-message-ttl"] = (long)delay.TotalMilliseconds;
            retry["x-dead-letter-exchange"] = string.Empty; // varsayılan exchange: routing key = kuyruk adı
            retry["x-dead-letter-routing-key"] = _options.Queue;
            await channel.QueueDeclareAsync(_options.RetryQueue(delay), durable: true, exclusive: false, autoDelete: false, retry, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (string eventName in events)
            await channel.QueueBindAsync(_options.Queue, _options.Exchange, eventName, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Dictionary<string, object?> QueueArguments() => new(StringComparer.Ordinal) { ["x-queue-type"] = _options.QueueType };
}
