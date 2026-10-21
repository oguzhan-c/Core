using System.Diagnostics;
using System.Text;
using Confluent.Kafka;

namespace CanFx.EventBus.Kafka;

/// <summary>
/// <c>{tüketici}.dlq</c> topic'i. Kafka'da okunan mesaj silinmez: hangi ölü mesajların gönderildiğini
/// <c>{tüketici}.dlq-replay</c> consumer group'unun offset'i tutar. Bekleyen sayı = son offset − bu grubun offset'i.
/// Yeniden gönderim: mesaj bu tüketicinin retry topic'ine (yalnızca bu servis okur) bekleme zamanı olmadan yazılır.
/// Filtreye uymayanlar DLQ topic'inin sonuna yeniden yazılır (bekleyen olarak kalırlar).
/// </summary>
public sealed class KafkaDeadLetterQueue : IDeadLetterQueue
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly KafkaInfrastructure _kafka;
    private readonly KafkaOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KafkaDeadLetterQueue(KafkaInfrastructure kafka, KafkaOptions options)
    {
        _kafka = kafka;
        _options = options;
    }

    public string Consumer => _options.ResolveConsumerName();

    public Task<long?> CountAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(_options.DeadLetterConsumerConfig()).Build();
            List<TopicPartition> partitions = Partitions();
            if (partitions.Count == 0)
                return (long?)null;

            long waiting = 0;
            List<TopicPartitionOffset> committed = consumer.Committed(partitions, Timeout);
            foreach (TopicPartition partition in partitions)
            {
                WatermarkOffsets watermarks = consumer.QueryWatermarkOffsets(partition, Timeout);
                Offset from = committed.FirstOrDefault(c => c.TopicPartition == partition)?.Offset ?? Offset.Unset;
                long start = from.IsSpecial ? watermarks.Low.Value : Math.Max(from.Value, watermarks.Low.Value);
                waiting += Math.Max(0, watermarks.High.Value - start);
            }

            return (long?)waiting;
        }, cancellationToken);

    public async Task<DeadLetterReplayResult> ReplayAsync(DeadLetterReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DeadLetterReplayOptions();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); // aynı süreçte iki gönderim aynı mesajı iki kez göndermesin
        try
        {
            return await Task.Factory.StartNew(() => Replay(options, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DeadLetterReplayResult Replay(DeadLetterReplayOptions options, CancellationToken cancellationToken)
    {
        List<TopicPartition> partitions = Partitions();
        if (partitions.Count == 0)
            return new DeadLetterReplayResult(0, 0);

        using IConsumer<string, byte[]> consumer = new ConsumerBuilder<string, byte[]>(_options.DeadLetterConsumerConfig()).Build();

        // Başlangıçtaki son offset'lere kadar: sona geri yazılan (atlanan) mesajlar tekrar okunmaz
        List<TopicPartitionOffset> committed = consumer.Committed(partitions, Timeout);
        var ends = new Dictionary<TopicPartition, long>();
        var assignment = new List<TopicPartitionOffset>();
        foreach (TopicPartition partition in partitions)
        {
            WatermarkOffsets watermarks = consumer.QueryWatermarkOffsets(partition, Timeout);
            Offset from = committed.FirstOrDefault(c => c.TopicPartition == partition)?.Offset ?? Offset.Unset;
            long start = from.IsSpecial ? watermarks.Low.Value : Math.Max(from.Value, watermarks.Low.Value);
            if (start < watermarks.High.Value)
            {
                ends[partition] = watermarks.High.Value;
                assignment.Add(new TopicPartitionOffset(partition, start));
            }
        }

        int replayed = 0, skipped = 0;
        if (assignment.Count == 0)
            return new DeadLetterReplayResult(0, 0);

        consumer.Assign(assignment);
        DateTime idleUntil = DateTime.UtcNow + Timeout;
        try
        {
            while (ends.Count > 0 && replayed < options.MaxMessages && replayed + skipped < options.MaxScanned && !cancellationToken.IsCancellationRequested)
            {
                ConsumeResult<string, byte[]>? result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result?.Message is null)
                {
                    if (DateTime.UtcNow > idleUntil)
                        break; // broker yanıt vermiyor; kalanlar bir sonraki gönderimde
                    continue;
                }

                idleUntil = DateTime.UtcNow + Timeout;
                if (!ends.TryGetValue(result.TopicPartition, out long end) || result.Offset.Value >= end)
                {
                    ends.Remove(result.TopicPartition);
                    consumer.Pause([result.TopicPartition]);
                    continue;
                }

                Dictionary<string, string> headers = KafkaInfrastructure.ReadHeaders(result.Message.Headers);
                var dead = new DeadLetterMessage(headers);
                bool replay = options.Filter?.Invoke(dead) ?? true;

                // önce yaz (onay beklenir), sonra offset: arada çökerse mesaj en kötü iki kez gönderilir, kaybolmaz
                _kafka.Producer.ProduceAsync(
                    replay ? _options.RetryTopic : _options.DeadLetterTopic,
                    new Message<string, byte[]>
                    {
                        Key = result.Message.Key,
                        Value = result.Message.Value,
                        Headers = KafkaInfrastructure.ToKafkaHeaders(replay ? EventHeaders.ForReplay(headers, Consumer) : headers),
                    },
                    cancellationToken).GetAwaiter().GetResult();
                consumer.Commit([new TopicPartitionOffset(result.TopicPartition, result.Offset + 1)]);

                if (result.Offset.Value + 1 >= end)
                {
                    ends.Remove(result.TopicPartition);
                    consumer.Pause([result.TopicPartition]);
                }

                if (replay)
                {
                    replayed++;
                    EventBusTelemetry.Replayed.Add(1, new TagList { { "can.event", dead.EventName }, { "can.consumer", Consumer } });
                }
                else
                {
                    skipped++;
                }
            }
        }
        finally
        {
            consumer.Close();
        }

        return new DeadLetterReplayResult(replayed, skipped);
    }

    private List<TopicPartition> Partitions()
    {
        Metadata metadata = _kafka.Admin.GetMetadata(_options.DeadLetterTopic, Timeout);
        TopicMetadata? topic = metadata.Topics.FirstOrDefault(t => t.Topic == _options.DeadLetterTopic);
        if (topic is null || topic.Error.Code != ErrorCode.NoError)
            return [];
        return topic.Partitions.Select(p => new TopicPartition(_options.DeadLetterTopic, new Partition(p.PartitionId))).ToList();
    }
}

/// <summary>Broker bağlantısı (metadata), tüketim döngüsü ve DLQ'da bekleyen mesajlar.</summary>
public sealed class KafkaBrokerHealth : IEventBrokerHealth
{
    private readonly KafkaInfrastructure _kafka;
    private readonly KafkaDeadLetterQueue _deadLetters;
    private readonly KafkaConsumerService? _consumer;

    public KafkaBrokerHealth(KafkaInfrastructure kafka, KafkaDeadLetterQueue deadLetters, IServiceProvider services)
    {
        _kafka = kafka;
        _deadLetters = deadLetters;
        _consumer = (KafkaConsumerService?)services.GetService(typeof(KafkaConsumerService));
    }

    public async Task<EventBrokerHealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        bool? consuming = _consumer?.IsConsuming;
        try
        {
            Metadata metadata = await Task.Run(() => _kafka.Admin.GetMetadata(TimeSpan.FromSeconds(5)), cancellationToken).ConfigureAwait(false);
            if (metadata.Brokers.Count == 0)
                return new EventBrokerHealthReport("kafka", false, consuming, null, "Broker listesi boş.");
            long? dead = _consumer is null ? null : await _deadLetters.CountAsync(cancellationToken).ConfigureAwait(false);
            return new EventBrokerHealthReport("kafka", true, consuming, dead);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new EventBrokerHealthReport("kafka", false, consuming, null, exception.Message);
        }
    }
}
