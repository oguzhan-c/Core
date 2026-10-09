using System.Collections.Concurrent;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Can.Core.EventBus.Kafka;

/// <summary>Uygulama başına tek producer (thread-safe; Kafka önerisi) ve topic oluşturma.</summary>
public sealed class KafkaInfrastructure : IDisposable
{
    private readonly KafkaOptions _options;
    private readonly Lazy<IProducer<string, byte[]>> _producer;
    private readonly ConcurrentDictionary<string, byte> _knownTopics = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _adminGate = new(1, 1);

    public KafkaInfrastructure(KafkaOptions options)
    {
        _options = options;
        _producer = new Lazy<IProducer<string, byte[]>>(() => new ProducerBuilder<string, byte[]>(options.ProducerConfig()).Build());
    }

    public IProducer<string, byte[]> Producer => _producer.Value;

    /// <summary>Eksik topic'leri oluşturur (<see cref="KafkaOptions.CreateTopics"/> kapalıysa bir şey yapmaz).</summary>
    public async Task EnsureTopicsAsync(IEnumerable<string> topics, CancellationToken cancellationToken)
    {
        if (!_options.CreateTopics)
            return;

        string[] missing = topics.Where(t => !_knownTopics.ContainsKey(t)).Distinct(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
            return;

        await _adminGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            missing = missing.Where(t => !_knownTopics.ContainsKey(t)).ToArray();
            if (missing.Length == 0)
                return;

            using IAdminClient admin = new AdminClientBuilder(_options.AdminConfig()).Build();
            try
            {
                await admin.CreateTopicsAsync(missing.Select(t => new TopicSpecification
                {
                    Name = t,
                    NumPartitions = _options.Partitions,
                    ReplicationFactor = _options.ReplicationFactor,
                })).ConfigureAwait(false);
            }
            catch (CreateTopicsException exception)
                when (exception.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
            {
                // zaten var: sorun değil
            }

            foreach (string topic in missing)
                _knownTopics[topic] = 0;
        }
        finally
        {
            _adminGate.Release();
        }
    }

    internal static Headers ToKafkaHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var result = new Headers();
        foreach ((string key, string value) in headers)
            result.Add(key, Encoding.UTF8.GetBytes(value));
        return result;
    }

    internal static Dictionary<string, string> ReadHeaders(Headers? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (headers is null)
            return result;
        foreach (IHeader header in headers)
            result[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes()); // aynı anahtar birden çok ise sonuncusu
        return result;
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Flush(TimeSpan.FromSeconds(10)); // kuyruktaki mesajlar gitsin
            _producer.Value.Dispose();
        }

        _adminGate.Dispose();
    }
}

/// <summary>Event'i adının topic'ine yazar; anahtar event kimliği (partition'lara eşit dağılır).</summary>
public sealed class KafkaEventTransport : IEventTransport
{
    private readonly KafkaInfrastructure _kafka;
    private readonly KafkaOptions _options;

    public KafkaEventTransport(KafkaInfrastructure kafka, KafkaOptions options)
    {
        _kafka = kafka;
        _options = options;
    }

    public async Task SendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        string topic = _options.TopicOf(envelope.EventName);
        await _kafka.EnsureTopicsAsync([topic], cancellationToken).ConfigureAwait(false);

        var message = new Message<string, byte[]>
        {
            Key = envelope.EventId.ToString("D"),
            Value = Encoding.UTF8.GetBytes(envelope.Payload),
            Headers = KafkaInfrastructure.ToKafkaHeaders(EventHeaders.From(envelope)),
            Timestamp = new Timestamp(envelope.OccurredAt),
        };
        await _kafka.Producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false);
    }
}
