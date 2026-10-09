using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.EventBus.AzureServiceBus;

/// <summary>
/// Subscription'ı dinler (peek-lock, otomatik tamamlama kapalı). Sonuca göre: tamamla / zamanlanmış kopya gönder +
/// tamamla / dead-letter (nedeni ve hata başlıklarıyla). Sonuçlandırma başarısızsa mesaj bırakılır (abandon) ve
/// Service Bus tekrar teslim eder.
/// </summary>
public sealed partial class AzureServiceBusConsumerService : BackgroundService
{
    private readonly AzureServiceBusInfrastructure _bus;
    private readonly AzureServiceBusOptions _options;
    private readonly EventConsumer _consumer;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IServiceProvider _services;
    private readonly ILogger<AzureServiceBusConsumerService> _logger;
    private ServiceBusProcessor? _processor;

    public AzureServiceBusConsumerService(
        AzureServiceBusInfrastructure bus,
        AzureServiceBusOptions options,
        EventDispatcher dispatcher,
        IServiceProvider services,
        ILogger<AzureServiceBusConsumerService> logger,
        TimeProvider? timeProvider = null)
    {
        _bus = bus;
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

        TimeSpan wait = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.ManageTopology)
                    await EnsureTopologyAsync(events, stoppingToken).ConfigureAwait(false);

                _processor = _bus.Client.CreateProcessor(_options.TopicName, _options.Subscription, new ServiceBusProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = _options.MaxConcurrentCalls,
                    PrefetchCount = _options.PrefetchCount,
                    MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
                    ReceiveMode = ServiceBusReceiveMode.PeekLock,
                });
                _processor.ProcessMessageAsync += args => HandleAsync(args, stoppingToken);
                _processor.ProcessErrorAsync += args =>
                {
                    LogProcessorError(args.Exception, args.ErrorSource.ToString(), args.EntityPath);
                    return Task.CompletedTask;
                };
                await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
                LogStarted(_consumer.Name, events.Count);
                _started.TrySetResult();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogConnectFailed(exception, wait);
                if (_processor is not null)
                {
                    await _processor.DisposeAsync().ConfigureAwait(false);
                    _processor = null;
                }

                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
                wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
            }
        }
    }

    private async Task HandleAsync(ProcessMessageEventArgs args, CancellationToken stoppingToken)
    {
        ServiceBusReceivedMessage message = args.Message;
        Dictionary<string, string> headers = AzureServiceBusInfrastructure.ReadHeaders(message);
        string payload = message.Body.ToString();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, args.CancellationToken);

        ConsumeOutcome outcome;
        EventEnvelope? envelope = null;
        try
        {
            envelope = EventHeaders.ToEnvelope(headers, payload);
            outcome = await _consumer.ConsumeAsync(envelope, EventHeaders.AttemptOf(headers), linked.Token).ConfigureAwait(false);
        }
        catch (FormatException exception)
        {
            outcome = new ConsumeOutcome(ConsumeAction.DeadLetter, 1, Error: exception, Reason: "invalid-message");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return; // kapanıyor: kilit süresi dolunca mesaj tekrar teslim edilir
        }

        try
        {
            switch (outcome.Action)
            {
                case ConsumeAction.Retry:
                    DateTimeOffset at = _consumer.Time.GetUtcNow() + outcome.Delay;
                    int next = outcome.Attempt + 1;
                    // yalnızca bu subscription'ın "can.retry" kuralı alır; kimlik denemeye özel (yinelenen tespitine takılmasın)
                    ServiceBusMessage retry = AzureServiceBusInfrastructure.ToMessage(
                        envelope!.EventId,
                        AzureServiceBusOptions.RetrySubject,
                        $"{envelope.EventId:D}:{_consumer.Name}:{next}",
                        EventHeaders.ForRetry(headers, _consumer.Name, next, at),
                        payload);
                    await _bus.Sender.ScheduleMessageAsync(retry, at, linked.Token).ConfigureAwait(false);
                    await args.CompleteMessageAsync(message, linked.Token).ConfigureAwait(false);
                    break;

                case ConsumeAction.DeadLetter:
                    Dictionary<string, string> failed = EventHeaders.ForDeadLetter(headers, _consumer.Name, outcome.Reason ?? "failed", outcome.Error, _consumer.Time.GetUtcNow());
                    var properties = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [EventHeaders.Error] = failed[EventHeaders.Error],
                        [EventHeaders.ErrorType] = failed[EventHeaders.ErrorType],
                        [EventHeaders.FailedAt] = failed[EventHeaders.FailedAt],
                        [EventHeaders.Consumer] = _consumer.Name,
                    };
                    await args.DeadLetterMessageAsync(message, properties, outcome.Reason ?? "failed", Truncate(outcome.Error?.Message, 1024), linked.Token).ConfigureAwait(false);
                    break;

                default:
                    await args.CompleteMessageAsync(message, linked.Token).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !linked.IsCancellationRequested)
        {
            LogSettleFailed(exception, envelope?.EventName ?? "?", _consumer.Name);
            await args.AbandonMessageAsync(message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Topic ve subscription yoksa oluşturur; kuralları eşitler: her dinlenen event için <c>Subject</c> correlation
    /// kuralı + bu subscription'a özel retry kuralı. Yeni subscription'daki her şeyi geçiren <c>$Default</c> kuralı silinir.
    /// </summary>
    private async Task EnsureTopologyAsync(IReadOnlyList<string> events, CancellationToken cancellationToken)
    {
        ServiceBusAdministrationClient admin = _bus.CreateAdministrationClient();
        string topic = _options.TopicName;
        string subscription = _options.Subscription;

        if (!(await admin.TopicExistsAsync(topic, cancellationToken).ConfigureAwait(false)).Value)
            await IgnoreExistsAsync(() => admin.CreateTopicAsync(topic, cancellationToken)).ConfigureAwait(false);

        var desired = new Dictionary<string, CorrelationRuleFilter>(StringComparer.Ordinal);
        foreach (string eventName in events)
            desired[AzureServiceBusInfrastructure.RuleName(eventName)] = new CorrelationRuleFilter { Subject = eventName };
        var retry = new CorrelationRuleFilter { Subject = AzureServiceBusOptions.RetrySubject };
        retry.ApplicationProperties[EventHeaders.Consumer] = subscription;
        desired["can-retry"] = retry;

        if (!(await admin.SubscriptionExistsAsync(topic, subscription, cancellationToken).ConfigureAwait(false)).Value)
        {
            KeyValuePair<string, CorrelationRuleFilter> first = desired.First();
            await IgnoreExistsAsync(() => admin.CreateSubscriptionAsync(
                new CreateSubscriptionOptions(topic, subscription) { MaxDeliveryCount = _options.MaxDeliveryCount },
                new CreateRuleOptions(first.Key, first.Value),
                cancellationToken)).ConfigureAwait(false);
        }

        var existing = new HashSet<string>(StringComparer.Ordinal);
        await foreach (RuleProperties rule in admin.GetRulesAsync(topic, subscription, cancellationToken).ConfigureAwait(false))
        {
            if (desired.ContainsKey(rule.Name))
                existing.Add(rule.Name);
            else
                await admin.DeleteRuleAsync(topic, subscription, rule.Name, cancellationToken).ConfigureAwait(false); // $Default ve artık dinlenmeyenler
        }

        foreach ((string name, CorrelationRuleFilter filter) in desired)
        {
            if (!existing.Contains(name))
                await IgnoreExistsAsync(() => admin.CreateRuleAsync(topic, subscription, new CreateRuleOptions(name, filter), cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task IgnoreExistsAsync<T>(Func<Task<T>> create)
    {
        try
        {
            await create().ConfigureAwait(false);
        }
        catch (ServiceBusException exception) when (exception.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // başka bir örnek aynı anda oluşturdu
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
        }
    }

    private static string? Truncate(string? text, int length) => text is null || text.Length <= length ? text : text[..length];

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken).ConfigureAwait(false);
            await _processor.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Service Bus tüketicisi {Consumer} {Count} event için dinliyor.")]
    private partial void LogStarted(string consumer, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service Bus tüketicisi {Consumer}: dinlenecek event yok (handler kayıtlı değil); tüketici başlatılmadı.")]
    private partial void LogNothingToConsume(string consumer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service Bus'a bağlanılamadı ya da topoloji kurulamadı; {Wait} sonra tekrar denenecek.")]
    private partial void LogConnectFailed(Exception exception, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Service Bus işlemci hatası ({Source}, {EntityPath}).")]
    private partial void LogProcessorError(Exception exception, string source, string entityPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "{EventName} {Consumer} tüketicisinde sonuçlandırılamadı; mesaj bırakıldı (tekrar teslim edilecek).")]
    private partial void LogSettleFailed(Exception exception, string eventName, string consumer);
}

public static class AzureServiceBusServiceCollectionExtensions
{
    /// <summary>
    /// Event bus taşıyıcısını Azure Service Bus yapar. <c>AddCanEventBus(...)</c>'tan sonra çağır.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanEventBus(typeof(OrderShipped).Assembly);
    /// builder.Services.AddCanInbox&lt;AppDbContext&gt;();
    /// builder.Services.AddCanAzureServiceBusTransport(o =&gt;
    /// {
    ///     o.ConnectionString = builder.Configuration["EventBus:AzureServiceBus:ConnectionString"]; // user-secrets
    ///     o.ConsumerName = "billing";
    /// });
    /// // ya da yönetilen kimlik: o.FullyQualifiedNamespace = "ornek.servicebus.windows.net"; o.Credential = new DefaultAzureCredential();
    /// </code>
    /// </example>
    public static IServiceCollection AddCanAzureServiceBusTransport(this IServiceCollection services, Action<AzureServiceBusOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AzureServiceBusOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString) && (string.IsNullOrWhiteSpace(options.FullyQualifiedNamespace) || options.Credential is null))
            throw new InvalidOperationException("Azure Service Bus için ConnectionString ya da FullyQualifiedNamespace + Credential gerekli.");

        services.RemoveAll<AzureServiceBusOptions>();
        services.AddSingleton(options);
        services.TryAddSingleton<AzureServiceBusInfrastructure>();
        services.AddCanEventTransport<AzureServiceBusEventTransport>();
        if (options.EnableConsumer)
            services.AddSingleton<IHostedService, AzureServiceBusConsumerService>();
        return services;
    }
}
