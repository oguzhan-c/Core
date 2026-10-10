using global::RabbitMQ.Client;
using global::RabbitMQ.Client.Exceptions;

namespace Can.Core.EventBus.RabbitMQ;

/// <summary>
/// <c>{kuyruk}.dlq</c>. Yeniden gönderim: mesaj varsayılan exchange üzerinden doğrudan bu servisin ana kuyruğuna
/// yayınlanır (exchange'e değil: diğer servisler tekrar almaz). Filtreye uymayanlar DLQ'nun sonuna yeniden yazılır.
/// </summary>
public sealed class RabbitMqDeadLetterQueue : IDeadLetterQueue
{
    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;

    public RabbitMqDeadLetterQueue(RabbitMqConnection connection, RabbitMqOptions options)
    {
        _connection = connection;
        _options = options;
    }

    public string Consumer => _options.Queue;

    public async Task<long?> CountAsync(CancellationToken cancellationToken = default)
    {
        IConnection connection = await _connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            return (await channel.QueueDeclarePassiveAsync(_options.DeadLetterQueue, cancellationToken).ConfigureAwait(false)).MessageCount;
        }
        catch (OperationInterruptedException)
        {
            return null; // DLQ henüz yok (tüketici hiç başlamadı)
        }
    }

    public async Task<DeadLetterReplayResult> ReplayAsync(DeadLetterReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DeadLetterReplayOptions();
        IConnection connection = await _connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using IChannel channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        uint waiting = (await channel.QueueDeclarePassiveAsync(_options.DeadLetterQueue, cancellationToken).ConfigureAwait(false)).MessageCount;
        IChannel publisher = await _connection.GetPublisherAsync(cancellationToken).ConfigureAwait(false);

        // Başta bekleyen sayı kadar: sona yeniden yazılan (atlanan) mesajlar tekrar okunmaz
        long limit = Math.Min(waiting, (long)options.MaxScanned);
        int replayed = 0, skipped = 0;
        for (long i = 0; i < limit && replayed < options.MaxMessages; i++)
        {
            BasicGetResult? message = await channel.BasicGetAsync(_options.DeadLetterQueue, autoAck: false, cancellationToken).ConfigureAwait(false);
            if (message is null)
                break;

            Dictionary<string, string> headers = RabbitMqMessages.ReadHeaders(message.BasicProperties);
            var dead = new DeadLetterMessage(headers);
            bool replay = options.Filter?.Invoke(dead) ?? true;
            Dictionary<string, string> next = replay ? EventHeaders.ForReplay(headers, Consumer) : headers;
            BasicProperties properties = RabbitMqMessages.Properties(
                dead.EventId ?? Guid.Empty,
                dead.EventName ?? message.BasicProperties.Type ?? "unknown",
                next,
                DateTimeOffset.FromUnixTimeSeconds(message.BasicProperties.Timestamp.UnixTime));

            try
            {
                // gönderim onayı gelmeden DLQ'dan silinmez
                await publisher.BasicPublishAsync(string.Empty, replay ? _options.Queue : _options.DeadLetterQueue, mandatory: true, properties, message.Body.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                await channel.BasicAckAsync(message.DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await channel.BasicNackAsync(message.DeliveryTag, multiple: false, requeue: true, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (replay)
            {
                replayed++;
                EventBusTelemetry.Replayed.Add(1, new System.Diagnostics.TagList { { "can.event", dead.EventName }, { "can.consumer", Consumer } });
            }
            else
            {
                skipped++;
            }
        }

        return new DeadLetterReplayResult(replayed, skipped);
    }
}

/// <summary>Bağlantı, tüketici ve DLQ durumu.</summary>
public sealed class RabbitMqBrokerHealth : IEventBrokerHealth
{
    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqDeadLetterQueue _deadLetters;
    private readonly RabbitMqConsumerService? _consumer;

    public RabbitMqBrokerHealth(RabbitMqConnection connection, RabbitMqDeadLetterQueue deadLetters, IServiceProvider services)
    {
        _connection = connection;
        _deadLetters = deadLetters;
        _consumer = (RabbitMqConsumerService?)services.GetService(typeof(RabbitMqConsumerService));
    }

    public async Task<EventBrokerHealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        bool? consuming = _consumer?.IsConsuming;
        try
        {
            IConnection connection = await _connection.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!connection.IsOpen)
                return new EventBrokerHealthReport("rabbitmq", false, consuming, null, connection.CloseReason?.ReplyText);
            long? dead = _consumer is null ? null : await _deadLetters.CountAsync(cancellationToken).ConfigureAwait(false);
            return new EventBrokerHealthReport("rabbitmq", true, consuming, dead);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new EventBrokerHealthReport("rabbitmq", false, consuming, null, exception.Message);
        }
    }
}
