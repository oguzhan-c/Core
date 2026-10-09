using System.Text;
using Confluent.Kafka;

namespace Can.Core.EventBus.Kafka;

/// <summary>
/// Kafka taşıyıcısı. Her event adı bir topic (<see cref="TopicPrefix"/> + ad); tüketici adı consumer group'tur
/// (aynı servisin örnekleri partition'ları paylaşır, farklı servisler her event'in kopyasını alır). Yeniden deneme
/// <c>{tüketici}.retry</c>, DLQ <c>{tüketici}.dlq</c> topic'leridir.
/// </summary>
public sealed class KafkaOptions : EventBrokerOptions
{
    /// <summary>Broker adresleri (<c>host1:9092,host2:9092</c>).</summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>Topic adlarının ön eki (ör. ortam: <c>prod.</c>).</summary>
    public string TopicPrefix { get; set; } = string.Empty;

    /// <summary>Eksik topic'leri oluştur (broker'da otomatik oluşturma kapalıysa gerekli; yetki ister).</summary>
    public bool CreateTopics { get; set; } = true;

    public int Partitions { get; set; } = 3;

    public short ReplicationFactor { get; set; } = 1;

    /// <summary>
    /// Consumer group protokolü: <c>Classic</c> (varsayılan) ya da <c>Consumer</c> (KIP-848; broker da desteklemeli,
    /// yeniden dengeleme çok daha hızlı).
    /// </summary>
    public GroupProtocol? GroupProtocol { get; set; }

    /// <summary>Producer ayarları (SASL/SSL, sıkıştırma ...). Bizim varsayılanlarımızdan sonra uygulanır.</summary>
    public Action<ProducerConfig>? ConfigureProducer { get; set; }

    public Action<ConsumerConfig>? ConfigureConsumer { get; set; }

    public Action<AdminClientConfig>? ConfigureAdmin { get; set; }

    /// <summary>Event adının topic'i (Kafka'nın izin verdiği karakterler: harf, rakam, <c>.</c>, <c>_</c>, <c>-</c>).</summary>
    public string TopicOf(string eventName) => TopicPrefix + Sanitize(eventName);

    internal string RetryTopic => TopicPrefix + Sanitize(ResolveConsumerName()) + ".retry";

    internal string DeadLetterTopic => TopicPrefix + Sanitize(ResolveConsumerName()) + ".dlq";

    internal static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        string result = builder.ToString();
        return result.Length > 200 ? result[..200] : result;
    }

    internal ProducerConfig ProducerConfig()
    {
        var config = new ProducerConfig
        {
            BootstrapServers = BootstrapServers,
            ClientId = ResolveConsumerName(),
            Acks = Acks.All,              // tüm eş kopyalar yazınca onay
            EnableIdempotence = true,     // yeniden denemede çift kayıt yok, sıra korunur
            LingerMs = 5,
        };
        ConfigureProducer?.Invoke(config);
        return config;
    }

    internal ConsumerConfig ConsumerConfig()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = ResolveConsumerName(),
            ClientId = ResolveConsumerName(),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // en az bir kez: offset ancak işlendikten sonra işaretlenir (StoreOffset), arka plan commit'i onları yazar
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            EnablePartitionEof = false,
        };
        if (GroupProtocol is { } protocol)
            config.GroupProtocol = protocol;
        ConfigureConsumer?.Invoke(config);
        return config;
    }

    internal AdminClientConfig AdminConfig()
    {
        var config = new AdminClientConfig { BootstrapServers = BootstrapServers };
        ConfigureAdmin?.Invoke(config);
        return config;
    }
}
