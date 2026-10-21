using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace CanFx.EventBus.AzureServiceBus;

/// <summary>
/// Subscription'ın yerleşik dead-letter kuyruğu. Yeniden gönderim: mesajın kopyası topic'e <c>can.retry</c> konusu ve bu
/// subscription'ın adıyla gönderilir (yalnızca bu subscription'ın kuralı alır), sonra DLQ'daki asıl tamamlanır.
/// Filtreye uymayanlar kilitli tutulur ve sonunda bırakılır (DLQ'da kalırlar).
/// </summary>
public sealed class AzureServiceBusDeadLetterQueue : IDeadLetterQueue
{
    private const int PeekLimit = 1000;

    private readonly AzureServiceBusInfrastructure _bus;
    private readonly AzureServiceBusOptions _options;

    public AzureServiceBusDeadLetterQueue(AzureServiceBusInfrastructure bus, AzureServiceBusOptions options)
    {
        _bus = bus;
        _options = options;
    }

    public string Consumer => _options.Subscription;

    /// <summary>
    /// <see cref="AzureServiceBusOptions.ManageTopology"/> açıksa (Manage yetkisi) tam sayı; değilse DLQ'ya göz atılarak
    /// sayılır (en çok 1000).
    /// </summary>
    public async Task<long?> CountAsync(CancellationToken cancellationToken = default)
    {
        if (_options.ManageTopology)
        {
            SubscriptionRuntimeProperties properties = await _bus.CreateAdministrationClient()
                .GetSubscriptionRuntimePropertiesAsync(_options.TopicName, _options.Subscription, cancellationToken).ConfigureAwait(false);
            return properties.DeadLetterMessageCount;
        }

        await using ServiceBusReceiver receiver = DeadLetterReceiver();
        long count = 0;
        while (count < PeekLimit)
        {
            IReadOnlyList<ServiceBusReceivedMessage> page = await receiver.PeekMessagesAsync(100, cancellationToken: cancellationToken).ConfigureAwait(false);
            count += page.Count;
            if (page.Count == 0)
                break;
        }

        return count;
    }

    public async Task<DeadLetterReplayResult> ReplayAsync(DeadLetterReplayOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DeadLetterReplayOptions();
        await using ServiceBusReceiver receiver = DeadLetterReceiver();
        var held = new List<ServiceBusReceivedMessage>();
        int replayed = 0;
        try
        {
            while (replayed < options.MaxMessages && replayed + held.Count < options.MaxScanned)
            {
                int batch = Math.Min(50, Math.Min(options.MaxMessages - replayed, options.MaxScanned - replayed - held.Count));
                IReadOnlyList<ServiceBusReceivedMessage> messages = await receiver
                    .ReceiveMessagesAsync(batch, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                if (messages.Count == 0)
                    break;

                foreach (ServiceBusReceivedMessage message in messages)
                {
                    Dictionary<string, string> headers = AzureServiceBusInfrastructure.ReadHeaders(message);
                    // Service Bus'ın kendi dead-letter'ları (ör. MaxDeliveryCountExceeded) bizim hata başlıklarımızı taşımaz
                    if (!headers.ContainsKey(EventHeaders.Error) && (message.DeadLetterErrorDescription ?? message.DeadLetterReason) is { Length: > 0 } reason)
                        headers[EventHeaders.Error] = reason;
                    if (!headers.ContainsKey(EventHeaders.ErrorType) && message.DeadLetterReason is { Length: > 0 } type)
                        headers[EventHeaders.ErrorType] = type;

                    var dead = new DeadLetterMessage(headers);
                    if (!(options.Filter?.Invoke(dead) ?? true) || dead.EventId is not { } eventId)
                    {
                        held.Add(message);
                        continue;
                    }

                    Dictionary<string, string> next = EventHeaders.ForReplay(headers, Consumer);
                    next.Remove("DeadLetterReason"); // Service Bus bunları uygulama özelliği olarak da yazar
                    next.Remove("DeadLetterErrorDescription");
                    ServiceBusMessage copy = AzureServiceBusInfrastructure.ToMessage(
                        eventId,
                        AzureServiceBusOptions.RetrySubject,
                        $"{eventId:D}:{Consumer}:replay:{Guid.NewGuid():N}",
                        next,
                        message.Body.ToString());
                    await _bus.Sender.SendMessageAsync(copy, cancellationToken).ConfigureAwait(false);
                    await receiver.CompleteMessageAsync(message, cancellationToken).ConfigureAwait(false);
                    replayed++;
                    EventBusTelemetry.Replayed.Add(1, new TagList { { "can.event", dead.EventName }, { "can.consumer", Consumer } });
                }
            }
        }
        finally
        {
            foreach (ServiceBusReceivedMessage message in held)
            {
                try
                {
                    await receiver.AbandonMessageAsync(message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                catch (ServiceBusException exception) when (exception.Reason == ServiceBusFailureReason.MessageLockLost)
                {
                    // kilit zaten düştü: mesaj DLQ'da, tekrar görünür
                }
            }
        }

        return new DeadLetterReplayResult(replayed, held.Count);
    }

    private ServiceBusReceiver DeadLetterReceiver() =>
        _bus.Client.CreateReceiver(_options.TopicName, _options.Subscription, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        });
}

/// <summary>
/// Bağlantı ve DLQ (subscription çalışma zamanı bilgileri; Manage yetkisi yoksa DLQ'ya göz atılır) ve işlemcinin durumu.
/// </summary>
public sealed class AzureServiceBusBrokerHealth : IEventBrokerHealth
{
    private readonly AzureServiceBusDeadLetterQueue _deadLetters;
    private readonly AzureServiceBusInfrastructure _bus;
    private readonly AzureServiceBusOptions _options;
    private readonly AzureServiceBusConsumerService? _consumer;

    public AzureServiceBusBrokerHealth(AzureServiceBusDeadLetterQueue deadLetters, AzureServiceBusInfrastructure bus, AzureServiceBusOptions options, IServiceProvider services)
    {
        _deadLetters = deadLetters;
        _bus = bus;
        _options = options;
        _consumer = (AzureServiceBusConsumerService?)services.GetService(typeof(AzureServiceBusConsumerService));
    }

    public async Task<EventBrokerHealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        bool? consuming = _consumer?.IsConsuming;
        try
        {
            if (_consumer is not null)
            {
                long? dead = await _deadLetters.CountAsync(cancellationToken).ConfigureAwait(false); // bağlantıyı da doğrular
                return new EventBrokerHealthReport("azure-servicebus", true, consuming, dead);
            }

            // yalnızca yayınlayan servis: topic'e ulaşılabiliyor mu (Manage yetkisi yoksa denetlenemez)
            if (_options.ManageTopology)
                await _bus.CreateAdministrationClient().GetTopicRuntimePropertiesAsync(_options.TopicName, cancellationToken).ConfigureAwait(false);
            return new EventBrokerHealthReport("azure-servicebus", true, null, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new EventBrokerHealthReport("azure-servicebus", false, consuming, null, exception.Message);
        }
    }
}
