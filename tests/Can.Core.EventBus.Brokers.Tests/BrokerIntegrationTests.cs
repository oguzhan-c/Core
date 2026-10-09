using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Can.Core.EventBus.AzureServiceBus;
using Can.Core.EventBus.Kafka;
using Can.Core.EventBus.RabbitMQ;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Can.Core.EventBus.Brokers.Tests;

/// <summary>
/// Gerçek RabbitMQ: <c>CAN_RABBITMQ_URL=amqp://guest:guest@localhost:5672/</c> verilmezse atlanır.
/// Her test kendi exchange/kuyruklarını kurar ve sonunda siler.
/// </summary>
public class RabbitMqIntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CAN_RABBITMQ_URL");

    private static async Task<(BrokerHost Host, RabbitMqOptions Options)> StartAsync()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Url), "CAN_RABBITMQ_URL verilmedi.");
        RabbitMqOptions? options = null;
        BrokerHost host = await BrokerHost.StartAsync(
            services => services.AddCanRabbitMqTransport(o =>
            {
                o.ConnectionString = Url!;
                o.Exchange = BrokerHost.UniqueName("it.events");
                o.ConsumerName = BrokerHost.UniqueName("it");
                o.ImmediateRetries = 0;
                o.RetryDelays.Clear();
                o.RetryDelays.Add(TimeSpan.FromSeconds(1));
                options = o;
            }),
            service => ((RabbitMqConsumerService)service).Started);
        return (host, options!);
    }

    private static async Task CleanupAsync(BrokerHost host, RabbitMqOptions options)
    {
        RabbitMqConnection connection = host.Services.GetRequiredService<RabbitMqConnection>();
        IConnection amqp = await connection.GetConnectionAsync(CancellationToken.None);
        await using IChannel channel = await amqp.CreateChannelAsync();
        foreach (string queue in new[] { options.Queue, options.DeadLetterQueue }.Concat(options.RetryDelays.Select(options.RetryQueue)))
            await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false);
        await channel.ExchangeDeleteAsync(options.Exchange);
    }

    [Fact]
    public async Task Delivers_event()
    {
        (BrokerHost host, RabbitMqOptions options) = await StartAsync();
        await using (host)
        {
            await BrokerScenarios.DeliversEventAsync(host);
            await CleanupAsync(host, options);
        }
    }

    [Fact]
    public async Task Ignores_duplicate_delivery()
    {
        (BrokerHost host, RabbitMqOptions options) = await StartAsync();
        await using (host)
        {
            await BrokerScenarios.IgnoresDuplicateDeliveryAsync(host);
            await CleanupAsync(host, options);
        }
    }

    [Fact]
    public async Task Retries_through_delay_queue_then_dead_letters()
    {
        (BrokerHost host, RabbitMqOptions options) = await StartAsync();
        await using (host)
        {
            IConnection amqp = await host.Services.GetRequiredService<RabbitMqConnection>().GetConnectionAsync(CancellationToken.None);
            await using IChannel channel = await amqp.CreateChannelAsync();

            await BrokerScenarios.RetriesThenDeadLettersAsync(host, async () =>
            {
                BasicGetResult? message = await channel.BasicGetAsync(options.DeadLetterQueue, autoAck: true);
                return message is null ? null : RabbitMqMessages.ReadHeaders(message.BasicProperties);
            });
            await CleanupAsync(host, options);
        }
    }
}

/// <summary>Gerçek Kafka: <c>CAN_KAFKA_BOOTSTRAP=localhost:9092</c> verilmezse atlanır. Topic'ler test başına ön ekli ve sonunda silinir.</summary>
public class KafkaIntegrationTests
{
    private static readonly string? Bootstrap = Environment.GetEnvironmentVariable("CAN_KAFKA_BOOTSTRAP");

    private static async Task<(BrokerHost Host, KafkaOptions Options)> StartAsync()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Bootstrap), "CAN_KAFKA_BOOTSTRAP verilmedi.");
        KafkaOptions? options = null;
        BrokerHost host = await BrokerHost.StartAsync(
            services => services.AddCanKafkaTransport(o =>
            {
                o.BootstrapServers = Bootstrap!;
                o.TopicPrefix = BrokerHost.UniqueName("it") + ".";
                o.ConsumerName = BrokerHost.UniqueName("it");
                o.Partitions = 1;
                o.ImmediateRetries = 0;
                o.RetryDelays.Clear();
                o.RetryDelays.Add(TimeSpan.FromSeconds(1));
                options = o;
            }),
            service => ((KafkaConsumerService)service).Started);
        return (host, options!);
    }

    private static async Task CleanupAsync(KafkaOptions options)
    {
        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Bootstrap }).Build();
        Metadata metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
        string[] topics = metadata.Topics.Select(t => t.Topic).Where(t => t.StartsWith(options.TopicPrefix, StringComparison.Ordinal)).ToArray();
        if (topics.Length > 0)
            await admin.DeleteTopicsAsync(topics);
    }

    [Fact]
    public async Task Delivers_event()
    {
        (BrokerHost host, KafkaOptions options) = await StartAsync();
        await using (host)
            await BrokerScenarios.DeliversEventAsync(host);
        await CleanupAsync(options);
    }

    [Fact]
    public async Task Ignores_duplicate_delivery()
    {
        (BrokerHost host, KafkaOptions options) = await StartAsync();
        await using (host)
            await BrokerScenarios.IgnoresDuplicateDeliveryAsync(host);
        await CleanupAsync(options);
    }

    [Fact]
    public async Task Retries_through_retry_topic_then_dead_letters()
    {
        (BrokerHost host, KafkaOptions options) = await StartAsync();
        await using (host)
        {
            using IConsumer<string, byte[]> dlq = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
            {
                BootstrapServers = Bootstrap,
                GroupId = BrokerHost.UniqueName("it-dlq"),
                AutoOffsetReset = AutoOffsetReset.Earliest,
            }).Build();
            dlq.Subscribe(options.DeadLetterTopic);

            await BrokerScenarios.RetriesThenDeadLettersAsync(host, () =>
            {
                ConsumeResult<string, byte[]>? result = dlq.Consume(TimeSpan.FromSeconds(1));
                return Task.FromResult<IReadOnlyDictionary<string, string>?>(result?.Message is null ? null : KafkaInfrastructure.ReadHeaders(result.Message.Headers));
            });
            dlq.Close();
        }

        await CleanupAsync(options);
    }
}

/// <summary>
/// Gerçek Azure Service Bus ya da emülatör: <c>CAN_AZURE_SERVICEBUS_CONNECTION</c> verilmezse atlanır. Her test kendi
/// topic'ini kurar ve sonunda siler (Manage yetkili bağlantı dizesi gerekir).
/// </summary>
public class AzureServiceBusIntegrationTests
{
    private static readonly string? Connection = Environment.GetEnvironmentVariable("CAN_AZURE_SERVICEBUS_CONNECTION");

    private static async Task<(BrokerHost Host, AzureServiceBusOptions Options)> StartAsync()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Connection), "CAN_AZURE_SERVICEBUS_CONNECTION verilmedi.");
        AzureServiceBusOptions? options = null;
        BrokerHost host = await BrokerHost.StartAsync(
            services => services.AddCanAzureServiceBusTransport(o =>
            {
                o.ConnectionString = Connection;
                o.TopicName = BrokerHost.UniqueName("it-events");
                o.ConsumerName = BrokerHost.UniqueName("it");
                o.ImmediateRetries = 0;
                o.RetryDelays.Clear();
                o.RetryDelays.Add(TimeSpan.FromSeconds(1));
                options = o;
            }),
            service => ((AzureServiceBusConsumerService)service).Started);
        return (host, options!);
    }

    private static async Task CleanupAsync(AzureServiceBusOptions options) =>
        await new ServiceBusAdministrationClient(Connection).DeleteTopicAsync(options.TopicName);

    [Fact]
    public async Task Delivers_event()
    {
        (BrokerHost host, AzureServiceBusOptions options) = await StartAsync();
        await using (host)
            await BrokerScenarios.DeliversEventAsync(host);
        await CleanupAsync(options);
    }

    [Fact]
    public async Task Ignores_duplicate_delivery()
    {
        (BrokerHost host, AzureServiceBusOptions options) = await StartAsync();
        await using (host)
            await BrokerScenarios.IgnoresDuplicateDeliveryAsync(host);
        await CleanupAsync(options);
    }

    [Fact]
    public async Task Retries_with_scheduled_copy_then_dead_letters()
    {
        (BrokerHost host, AzureServiceBusOptions options) = await StartAsync();
        await using (host)
        {
            ServiceBusClient client = host.Services.GetRequiredService<AzureServiceBusInfrastructure>().Client;
            await using ServiceBusReceiver dlq = client.CreateReceiver(options.TopicName, options.Subscription, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

            await BrokerScenarios.RetriesThenDeadLettersAsync(host, async () =>
            {
                ServiceBusReceivedMessage? message = await dlq.ReceiveMessageAsync(TimeSpan.FromSeconds(1));
                if (message is null)
                    return null;
                await dlq.CompleteMessageAsync(message);
                return AzureServiceBusInfrastructure.ReadHeaders(message);
            });
        }

        await CleanupAsync(options);
    }
}
