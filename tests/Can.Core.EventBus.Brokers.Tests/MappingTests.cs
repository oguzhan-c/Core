using System.Text;
using Azure.Messaging.ServiceBus;
using Can.Core.EventBus.AzureServiceBus;
using Can.Core.EventBus.Kafka;
using Can.Core.EventBus.RabbitMQ;
using RabbitMQ.Client;

namespace Can.Core.EventBus.Brokers.Tests;

/// <summary>Broker'sız: zarf ↔ mesaj eşlemeleri ve adlandırma.</summary>
public class MappingTests
{
    private static readonly EventEnvelope Sample = new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "sales.order-placed",
        "{\"id\":7}",
        "t-a",
        new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero))
    {
        Headers = new Dictionary<string, string> { ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" },
    };

    [Fact]
    public void RabbitMq_properties_and_headers_round_trip()
    {
        BasicProperties properties = RabbitMqMessages.Properties(Sample.EventId, Sample.EventName, EventHeaders.From(Sample), Sample.OccurredAt);
        Assert.Equal(Sample.EventId.ToString("D"), properties.MessageId);
        Assert.Equal("sales.order-placed", properties.Type);
        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);

        // alımda istemci metinleri byte[] verir; RabbitMQ'nun x- başlıkları alınmaz
        var received = new BasicProperties
        {
            MessageId = properties.MessageId,
            Type = properties.Type,
            Headers = properties.Headers!.ToDictionary(h => h.Key, h => (object?)Encoding.UTF8.GetBytes((string)h.Value!)),
        };
        received.Headers["x-death"] = new List<object> { "döngü" };
        received.Headers["x-delivery-count"] = 3L;

        Dictionary<string, string> headers = RabbitMqMessages.ReadHeaders(received);
        Assert.DoesNotContain(headers.Keys, k => k.StartsWith("x-", StringComparison.Ordinal));

        EventEnvelope back = EventHeaders.ToEnvelope(headers, Sample.Payload);
        Assert.Equal(Sample.EventId, back.EventId);
        Assert.Equal("t-a", back.TenantId);
        Assert.Equal(Sample.Headers["traceparent"], back.Headers["traceparent"]);
    }

    [Fact]
    public void RabbitMq_falls_back_to_message_properties_for_foreign_publishers()
    {
        var properties = new BasicProperties { MessageId = Sample.EventId.ToString(), Type = "sales.order-placed" };

        EventEnvelope envelope = EventHeaders.ToEnvelope(RabbitMqMessages.ReadHeaders(properties), "{}");

        Assert.Equal(Sample.EventId, envelope.EventId);
        Assert.Equal("sales.order-placed", envelope.EventName);
    }

    [Fact]
    public void RabbitMq_queue_names()
    {
        var options = new RabbitMqOptions { ConsumerName = "billing" };
        Assert.Equal("billing", options.Queue);
        Assert.Equal("billing.dlq", options.DeadLetterQueue);
        Assert.Equal("billing.retry.10s", options.RetryQueue(TimeSpan.FromSeconds(10)));
        Assert.Equal("billing.retry.3600s", options.RetryQueue(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Kafka_topic_names_are_sanitized()
    {
        var options = new KafkaOptions { ConsumerName = "billing api", TopicPrefix = "prod." };
        Assert.Equal("prod.sales.order-placed", options.TopicOf("sales.order-placed"));
        Assert.Equal("prod.Sales-Order--dendi", options.TopicOf("Sales/Order Ödendi")); // yalnızca ASCII harf/rakam . _ -
        Assert.Equal("prod.billing-api.retry", options.RetryTopic);
        Assert.Equal("prod.billing-api.dlq", options.DeadLetterTopic);
    }

    [Fact]
    public void Kafka_headers_round_trip()
    {
        Dictionary<string, string> headers = KafkaInfrastructure.ReadHeaders(KafkaInfrastructure.ToKafkaHeaders(EventHeaders.From(Sample, attempt: 3)));

        Assert.Equal(3, EventHeaders.AttemptOf(headers));
        EventEnvelope back = EventHeaders.ToEnvelope(headers, Sample.Payload);
        Assert.Equal(Sample.EventName, back.EventName);
        Assert.Equal(Sample.OccurredAt, back.OccurredAt);
    }

    [Fact]
    public void Kafka_consumer_commits_only_processed_offsets()
    {
        var options = new KafkaOptions { ConsumerName = "billing", GroupProtocol = Confluent.Kafka.GroupProtocol.Consumer };
        Confluent.Kafka.ConsumerConfig config = options.ConsumerConfig();

        Assert.Equal("billing", config.GroupId);
        Assert.False(config.EnableAutoOffsetStore);
        Assert.True(config.EnableAutoCommit);
        Assert.Equal(Confluent.Kafka.GroupProtocol.Consumer, config.GroupProtocol);
        Assert.True(options.ProducerConfig().EnableIdempotence);
        Assert.Equal(Confluent.Kafka.Acks.All, options.ProducerConfig().Acks);
    }

    [Fact]
    public void Azure_message_round_trip()
    {
        ServiceBusMessage message = AzureServiceBusInfrastructure.ToMessage(Sample.EventId, Sample.EventName, Sample.EventId.ToString("D"), EventHeaders.From(Sample), Sample.Payload);
        Assert.Equal("sales.order-placed", message.Subject);

        ServiceBusReceivedMessage received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: message.Body,
            messageId: message.MessageId,
            correlationId: message.CorrelationId,
            subject: message.Subject,
            properties: message.ApplicationProperties);

        EventEnvelope back = EventHeaders.ToEnvelope(AzureServiceBusInfrastructure.ReadHeaders(received), received.Body.ToString());
        Assert.Equal(Sample.EventId, back.EventId);
        Assert.Equal(Sample.Payload, back.Payload);
        Assert.Equal("t-a", back.TenantId);
    }

    [Fact]
    public void Azure_rule_names_are_short_and_stable()
    {
        string name = AzureServiceBusInfrastructure.RuleName("sales.order-placed");
        Assert.Equal(name, AzureServiceBusInfrastructure.RuleName("sales.order-placed"));
        Assert.NotEqual(name, AzureServiceBusInfrastructure.RuleName("sales.order-shipped"));
        Assert.True(name.Length <= 50);
    }
}
