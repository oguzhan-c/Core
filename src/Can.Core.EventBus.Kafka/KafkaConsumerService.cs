using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.EventBus.Kafka;

/// <summary>
/// Consumer group üyesi olarak event topic'lerini ve kendi retry topic'ini dinler. Mesajlar partition sırasıyla tek tek
/// işlenir; offset yalnızca işlendikten (ya da retry/DLQ topic'ine yazıldıktan) sonra işaretlenir.
/// </summary>
/// <remarks>
/// Kafka'da gecikmeli teslim yoktur: retry mesajı zamanı gelmeden okunursa o partition durdurulur (<c>Pause</c>) ve
/// mesaja geri sarılır (<c>Seek</c>); döngü kısa <c>Consume</c> çağrılarıyla dönmeyi sürdürür. Thread uyutulsaydı
/// <c>max.poll.interval.ms</c> (5 dk) aşılır, tüketici gruptan atılırdı.
/// </remarks>
public sealed partial class KafkaConsumerService : BackgroundService
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(1);

    private readonly KafkaInfrastructure _kafka;
    private readonly KafkaOptions _options;
    private readonly EventConsumer _consumer;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IServiceProvider _services;
    private readonly ILogger<KafkaConsumerService> _logger;

    public KafkaConsumerService(
        KafkaInfrastructure kafka,
        KafkaOptions options,
        EventDispatcher dispatcher,
        IServiceProvider services,
        ILogger<KafkaConsumerService> logger,
        TimeProvider? timeProvider = null)
    {
        _kafka = kafka;
        _options = options;
        _services = services;
        _logger = logger;
        _consumer = new EventConsumer(dispatcher, options, logger, timeProvider);
    }

    // Consume() bloklar: thread havuzunu meşgul etmemek için ayrı uzun ömürlü thread
    /// <summary>Kuyruk/topic/subscription hazır ve dinleniyor (testler ve sağlık kontrolü için).</summary>
    internal Task Started => _started.Task;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<string> events = EventConsumer.Subscriptions(_services, _options);
        if (events.Count == 0)
        {
            LogNothingToConsume(_consumer.Name);
            return;
        }

        string[] topics = [.. events.Select(_options.TopicOf), _options.RetryTopic];
        await WaitForTopicsAsync([.. topics, _options.DeadLetterTopic], stoppingToken).ConfigureAwait(false);
        if (stoppingToken.IsCancellationRequested)
            return;

        var paused = new Dictionary<TopicPartition, DateTimeOffset>();
        using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(_options.ConsumerConfig())
            .SetErrorHandler((_, error) => LogKafkaError(error.Code, error.Reason))
            .SetPartitionsRevokedHandler((_, revoked) =>
            {
                foreach (TopicPartitionOffset partition in revoked)
                    paused.Remove(partition.TopicPartition);
            })
            .SetPartitionsLostHandler((_, lost) =>
            {
                foreach (TopicPartitionOffset partition in lost)
                    paused.Remove(partition.TopicPartition);
            })
            .Build();

        consumer.Subscribe(topics);
        LogStarted(_consumer.Name, events.Count);
        _started.TrySetResult();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ResumeDue(consumer, paused);

                ConsumeResult<string, byte[]>? result;
                try
                {
                    result = consumer.Consume(PollTimeout);
                }
                catch (ConsumeException exception)
                {
                    LogKafkaError(exception.Error.Code, exception.Error.Reason);
                    continue;
                }

                if (result?.Message is null)
                    continue;

                await HandleAsync(consumer, result, paused, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close(); // grubu hemen terk et (yeniden dengeleme beklemesin), işaretli offset'ler yazılır
        }
    }

    private async Task HandleAsync(IConsumer<string, byte[]> consumer, ConsumeResult<string, byte[]> result, Dictionary<TopicPartition, DateTimeOffset> paused, CancellationToken stoppingToken)
    {
        Dictionary<string, string> headers = KafkaInfrastructure.ReadHeaders(result.Message.Headers);
        string payload = result.Message.Value is null ? string.Empty : Encoding.UTF8.GetString(result.Message.Value);

        if (result.Topic == _options.RetryTopic && EventHeaders.NotBeforeOf(headers) is { } notBefore && notBefore > _consumer.Time.GetUtcNow())
        {
            // zamanı gelmedi: partition'ı durdur, bu mesaja geri sar
            consumer.Pause([result.TopicPartition]);
            consumer.Seek(result.TopicPartitionOffset);
            paused[result.TopicPartition] = notBefore;
            return;
        }

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

        try
        {
            switch (outcome.Action)
            {
                case ConsumeAction.Retry:
                    DateTimeOffset at = _consumer.Time.GetUtcNow() + outcome.Delay;
                    await ProduceAsync(_options.RetryTopic, result, EventHeaders.ForRetry(headers, _consumer.Name, outcome.Attempt + 1, at), stoppingToken).ConfigureAwait(false);
                    break;
                case ConsumeAction.DeadLetter:
                    await ProduceAsync(_options.DeadLetterTopic, result, EventHeaders.ForDeadLetter(headers, _consumer.Name, outcome.Reason ?? "failed", outcome.Error, _consumer.Time.GetUtcNow()), stoppingToken)
                        .ConfigureAwait(false);
                    break;
            }

            consumer.StoreOffset(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // retry/DLQ topic'ine yazılamadı: offset işaretlenmez, mesaja geri sarılır ve biraz beklenir
            LogSettleFailed(exception, envelope?.EventName ?? "?", _consumer.Name);
            consumer.Seek(result.TopicPartitionOffset);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private Task ProduceAsync(string topic, ConsumeResult<string, byte[]> source, Dictionary<string, string> headers, CancellationToken cancellationToken) =>
        _kafka.Producer.ProduceAsync(
            topic,
            new Message<string, byte[]>
            {
                Key = source.Message.Key,
                Value = source.Message.Value,
                Headers = KafkaInfrastructure.ToKafkaHeaders(headers),
            },
            cancellationToken);

    private void ResumeDue(IConsumer<string, byte[]> consumer, Dictionary<TopicPartition, DateTimeOffset> paused)
    {
        if (paused.Count == 0)
            return;
        DateTimeOffset now = _consumer.Time.GetUtcNow();
        TopicPartition[] due = paused.Where(p => p.Value <= now).Select(p => p.Key).ToArray();
        if (due.Length == 0)
            return;
        foreach (TopicPartition partition in due)
            paused.Remove(partition);
        consumer.Resume(due);
    }

    private async Task WaitForTopicsAsync(string[] topics, CancellationToken stoppingToken)
    {
        TimeSpan wait = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _kafka.EnsureTopicsAsync(topics, stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogConnectFailed(exception, wait);
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
                wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kafka tüketicisi {Consumer} {Count} event için dinliyor.")]
    private partial void LogStarted(string consumer, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kafka tüketicisi {Consumer}: dinlenecek event yok (handler kayıtlı değil); tüketici başlatılmadı.")]
    private partial void LogNothingToConsume(string consumer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kafka topic'leri hazırlanamadı; {Wait} sonra tekrar denenecek.")]
    private partial void LogConnectFailed(Exception exception, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kafka hatası {Code}: {Reason}")]
    private partial void LogKafkaError(ErrorCode code, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "{EventName} {Consumer} tüketicisinde sonuçlandırılamadı (retry/DLQ topic'ine yazılamadı); mesaj tekrar okunacak.")]
    private partial void LogSettleFailed(Exception exception, string eventName, string consumer);
}

public static class KafkaServiceCollectionExtensions
{
    /// <summary>
    /// Event bus taşıyıcısını Kafka yapar. <c>AddCanEventBus(...)</c>'tan sonra çağır.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanEventBus(typeof(OrderShipped).Assembly);
    /// builder.Services.AddCanInbox&lt;AppDbContext&gt;();
    /// builder.Services.AddCanKafkaTransport(o =&gt;
    /// {
    ///     o.BootstrapServers = "localhost:9092";
    ///     o.ConsumerName = "billing";
    ///     o.ConfigureProducer = c =&gt; { c.SecurityProtocol = SecurityProtocol.SaslSsl; c.SaslMechanism = SaslMechanism.Plain; ... };
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanKafkaTransport(this IServiceCollection services, Action<KafkaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new KafkaOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.BootstrapServers))
            throw new InvalidOperationException("Kafka BootstrapServers boş.");

        services.RemoveAll<KafkaOptions>();
        services.AddSingleton(options);
        services.TryAddSingleton<KafkaInfrastructure>();
        services.AddCanEventTransport<KafkaEventTransport>();
        if (options.EnableConsumer)
            services.AddSingleton<IHostedService, KafkaConsumerService>();
        return services;
    }
}
