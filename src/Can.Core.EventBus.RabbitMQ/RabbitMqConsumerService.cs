using System.Text;
using global::RabbitMQ.Client;
using global::RabbitMQ.Client.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.EventBus.RabbitMQ;

/// <summary>
/// Kuyruğu dinler. Her mesaj: zarfa çevrilir → <see cref="EventConsumer"/> (inbox, anında denemeler) → sonuca göre
/// onay / bekleme kuyruğuna yayın / DLQ'ya yayın. Yeniden yayınlar gönderim onaylı ayrı kanaldan yapılır; ancak onay
/// gelince asıl mesaj ack'lenir (yayın başarısızsa mesaj kuyruğa geri bırakılır, kaybolmaz).
/// </summary>
public sealed partial class RabbitMqConsumerService : BackgroundService
{
    private readonly RabbitMqConnection _connection;
    private readonly RabbitMqTopology _topology;
    private readonly RabbitMqOptions _options;
    private readonly EventConsumer _consumer;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IServiceProvider _services;
    private readonly ILogger<RabbitMqConsumerService> _logger;
    private IChannel? _channel;

    public RabbitMqConsumerService(
        RabbitMqConnection connection,
        RabbitMqTopology topology,
        RabbitMqOptions options,
        EventDispatcher dispatcher,
        IServiceProvider services,
        ILogger<RabbitMqConsumerService> logger,
        TimeProvider? timeProvider = null)
    {
        _connection = connection;
        _topology = topology;
        _options = options;
        _services = services;
        _logger = logger;
        _consumer = new EventConsumer(dispatcher, options, logger, timeProvider);
    }

    /// <summary>Kuyruk/topic/subscription hazır ve dinleniyor (testler ve sağlık kontrolü için).</summary>
    internal Task Started => _started.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<string> events = EventConsumer.Subscriptions(_services, _options);
        if (events.Count == 0)
        {
            LogNothingToConsume(_consumer.Name);
            return;
        }

        // Broker açılışta kapalı olabilir: bağlanana kadar artan aralıklarla dene (uygulama çökmesin)
        TimeSpan wait = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartAsync(events, stoppingToken).ConfigureAwait(false);
                return; // bundan sonrası istemcinin otomatik kurtarmasında
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogConnectFailed(exception, wait);
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
                wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
            }
        }
    }

    private async Task StartAsync(IReadOnlyList<string> events, CancellationToken stoppingToken)
    {
        IConnection connection = await _connection.GetConnectionAsync(stoppingToken).ConfigureAwait(false);
        IChannel channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
        await _topology.EnsureConsumerAsync(channel, events, stoppingToken).ConfigureAwait(false);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, stoppingToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(_options.Queue, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);
        _channel = channel;
        LogStarted(_consumer.Name, events.Count);
        _started.TrySetResult();
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        // gövde yalnızca bu olay içinde geçerli: kopyala
        string payload = Encoding.UTF8.GetString(delivery.Body.Span);
        Dictionary<string, string> headers = RabbitMqMessages.ReadHeaders(delivery.BasicProperties);

        ConsumeOutcome outcome;
        EventEnvelope? envelope = null;
        try
        {
            envelope = EventHeaders.ToEnvelope(headers, payload);
            outcome = await _consumer.ConsumeAsync(envelope, EventHeaders.AttemptOf(headers), stoppingToken).ConfigureAwait(false);
        }
        catch (FormatException exception)
        {
            outcome = new ConsumeOutcome(ConsumeAction.DeadLetter, 1, Error: exception, Reason: "invalid-message");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return; // kapanıyor: ack yok, kanal kapanınca mesaj kuyruğa döner
        }

        try
        {
            switch (outcome.Action)
            {
                case ConsumeAction.Retry:
                    DateTimeOffset notBefore = _consumer.Time.GetUtcNow() + outcome.Delay;
                    await RepublishAsync(_options.RetryQueue(outcome.Delay), envelope!, EventHeaders.ForRetry(headers, _consumer.Name, outcome.Attempt + 1, notBefore), payload, stoppingToken)
                        .ConfigureAwait(false);
                    break;
                case ConsumeAction.DeadLetter:
                    Dictionary<string, string> failed = EventHeaders.ForDeadLetter(headers, _consumer.Name, outcome.Reason ?? "failed", outcome.Error, _consumer.Time.GetUtcNow());
                    await RepublishAsync(_options.DeadLetterQueue, envelope, failed, payload, stoppingToken).ConfigureAwait(false);
                    break;
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // bekleme kuyruğuna/DLQ'ya yazılamadı: mesajı geri bırak (nack sayacı artırmaz; birazdan tekrar gelir)
            LogSettleFailed(exception, envelope?.EventName ?? "?", _consumer.Name);
            if (channel.IsOpen)
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Varsayılan exchange üzerinden doğrudan kuyruğa; <c>mandatory</c>: kuyruk yoksa hata (sessizce kaybolmasın).</summary>
    private async Task RepublishAsync(string queue, EventEnvelope? envelope, Dictionary<string, string> headers, string payload, CancellationToken cancellationToken)
    {
        IChannel publisher = await _connection.GetPublisherAsync(cancellationToken).ConfigureAwait(false);
        Guid id = envelope?.EventId ?? (Guid.TryParse(headers.GetValueOrDefault(EventHeaders.EventId), out Guid parsed) ? parsed : Guid.Empty);
        BasicProperties properties = RabbitMqMessages.Properties(id, envelope?.EventName ?? headers.GetValueOrDefault(EventHeaders.EventName) ?? "unknown", headers, envelope?.OccurredAt ?? DateTimeOffset.UtcNow);
        await publisher.BasicPublishAsync(string.Empty, queue, mandatory: true, properties, Encoding.UTF8.GetBytes(payload), cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_channel is { IsOpen: true } channel)
            await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ tüketicisi {Consumer} {Count} event için dinliyor.")]
    private partial void LogStarted(string consumer, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ tüketicisi {Consumer}: dinlenecek event yok (handler kayıtlı değil); tüketici başlatılmadı.")]
    private partial void LogNothingToConsume(string consumer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ'ya bağlanılamadı; {Wait} sonra tekrar denenecek.")]
    private partial void LogConnectFailed(Exception exception, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Error, Message = "{EventName} {Consumer} tüketicisinde sonuçlandırılamadı (bekleme kuyruğu/DLQ yayını); mesaj kuyruğa geri bırakıldı.")]
    private partial void LogSettleFailed(Exception exception, string eventName, string consumer);
}
