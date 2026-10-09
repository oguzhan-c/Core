using System.Reflection;
using System.Text.Json;
using Can.Core.Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Can.Core.EventBus;

/// <summary>
/// Broker taşıyıcılarının ortak ayarları (RabbitMQ, Kafka, Azure Service Bus ve kendi yazacağın taşıyıcı).
/// </summary>
public abstract class EventBrokerOptions
{
    /// <summary>
    /// Tüketici adı: kuyruk / consumer group / subscription adı ve inbox anahtarı. Aynı servisin örnekleri aynı adı
    /// kullanır (iş paylaşırlar); farklı servisler farklı ad (her biri event'in kopyasını alır). Boşsa uygulamanın adı.
    /// </summary>
    public string? ConsumerName { get; set; }

    /// <summary>Mesaj dinlensin mi (yalnızca yayınlayan servislerde kapat).</summary>
    public bool EnableConsumer { get; set; } = true;

    /// <summary>
    /// Dinlenecek event adları. Boşsa: kayıtlı event'lerden handler'ı (<c>INotificationHandler&lt;T&gt;</c>) olanlar.
    /// </summary>
    public IList<string> Events { get; } = [];

    /// <summary>Hata alınca mesajı bırakmadan önce aynı süreçte hemen kaç kez daha denensin (geçici hatalar için).</summary>
    public int ImmediateRetries { get; set; } = 2;

    public TimeSpan ImmediateRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Gecikmeli yeniden denemeler: hâlâ hata alan mesaj bu sürelerden sonra tekrar gelir; hepsi tükenince DLQ'ya gider.
    /// Toplam deneme = <c>RetryDelays.Count + 1</c>. Boşsa ilk hatada DLQ.
    /// </summary>
    public IList<TimeSpan> RetryDelays { get; } =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    /// <summary>Kalıcı hata mı (tekrar denemenin anlamı yok → doğrudan DLQ). Varsayılan: JSON/biçim hataları ve <see cref="PermanentEventFailureException"/>.</summary>
    public Func<Exception, bool> IsPermanentFailure { get; set; } = DefaultIsPermanent;

    /// <summary>Inbox kayıtlıysa tekrar gelen event'leri ayıkla.</summary>
    public bool UseInbox { get; set; } = true;

    /// <summary>Tüketici adı (verilmediyse giriş assembly'sinin adı, küçük harf).</summary>
    public string ResolveConsumerName() =>
        !string.IsNullOrWhiteSpace(ConsumerName)
            ? ConsumerName
            : (Assembly.GetEntryAssembly()?.GetName().Name ?? "can-consumer").ToLowerInvariant();

    public static bool DefaultIsPermanent(Exception exception) =>
        exception is PermanentEventFailureException or JsonException or FormatException;
}

/// <summary>Handler'dan fırlatılırsa mesaj yeniden denenmez, doğrudan DLQ'ya gider (ör. geçersiz veri).</summary>
public sealed class PermanentEventFailureException : Exception
{
    public PermanentEventFailureException(string message)
        : base(message) { }

    public PermanentEventFailureException(string message, Exception innerException)
        : base(message, innerException) { }

    public PermanentEventFailureException() { }
}

public enum ConsumeAction
{
    /// <summary>İşlendi (ya da bu serviste karşılığı yok / daha önce işlenmiş): onayla.</summary>
    Complete,

    /// <summary><see cref="ConsumeOutcome.Delay"/> sonra yeniden dene.</summary>
    Retry,

    /// <summary>DLQ'ya gönder.</summary>
    DeadLetter,
}

/// <summary>Bir mesajın işlenme sonucu; taşıyıcı buna göre onaylar, yeniden kuyruğa koyar ya da DLQ'ya taşır.</summary>
public sealed record ConsumeOutcome(ConsumeAction Action, int Attempt, TimeSpan Delay = default, Exception? Error = null, string? Reason = null)
{
    public static ConsumeOutcome Completed(int attempt, string? reason = null) => new(ConsumeAction.Complete, attempt, Reason: reason);
}

/// <summary>
/// Broker'dan gelen mesajı işler: inbox ile tekrarları ayıklar, hatada hemen birkaç kez dener, yine olmazsa gecikmeli
/// yeniden deneme ya da DLQ kararı verir. Taşıyıcılar yalnızca bu kararı uygular (ack / retry kuyruğu / DLQ).
/// </summary>
public sealed partial class EventConsumer
{
    private readonly EventDispatcher _dispatcher;
    private readonly EventBrokerOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public EventConsumer(EventDispatcher dispatcher, EventBrokerOptions options, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        Name = options.ResolveConsumerName();
    }

    public string Name { get; }

    /// <summary>Toplam deneme hakkı.</summary>
    public int MaxAttempts => _options.RetryDelays.Count + 1;

    public TimeProvider Time => _time;

    /// <summary>Dinlenecek event adları (ayarda verilmediyse handler'ı olan kayıtlı event'ler).</summary>
    public static IReadOnlyList<string> Subscriptions(IServiceProvider services, EventBrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Events.Count > 0)
            return options.Events.Distinct(StringComparer.Ordinal).ToArray();

        EventTypeRegistry registry = services.GetRequiredService<EventTypeRegistry>();
        IServiceProviderIsService? isService = services.GetService<IServiceProviderIsService>();
        return registry.Names
            .Where(name =>
            {
                if (isService is null || !registry.TryGetType(name, out Type? type) || type is null)
                    return true;
                return isService.IsService(typeof(INotificationHandler<>).MakeGenericType(type));
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <param name="envelope">Gelen event.</param>
    /// <param name="attempt">Kaçıncı deneme (mesaj başlığından, 1'den başlar).</param>
    /// <param name="cancellationToken">İptal (uygulama kapanıyor).</param>
    public async Task<ConsumeOutcome> ConsumeAsync(EventEnvelope envelope, int attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        attempt = Math.Max(1, attempt);
        string? consumer = _options.UseInbox ? Name : null;

        for (int immediate = 0; ; immediate++)
        {
            try
            {
                DispatchResult result = await _dispatcher.DispatchAsync(envelope, consumer, cancellationToken).ConfigureAwait(false);
                return result switch
                {
                    DispatchResult.Unknown => ConsumeOutcome.Completed(attempt, "unknown-event"),
                    DispatchResult.Duplicate => ConsumeOutcome.Completed(attempt, "duplicate"),
                    _ => ConsumeOutcome.Completed(attempt),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // kapanıyor: mesaj onaylanmaz, broker tekrar verir
            }
            catch (Exception exception)
            {
                if (_options.IsPermanentFailure(exception))
                {
                    LogPermanent(exception, envelope.EventName, envelope.EventId, Name);
                    return new ConsumeOutcome(ConsumeAction.DeadLetter, attempt, Error: exception, Reason: "permanent-failure");
                }

                if (immediate < _options.ImmediateRetries)
                {
                    await Task.Delay(_options.ImmediateRetryDelay * (immediate + 1), _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (attempt >= MaxAttempts)
                {
                    LogExhausted(exception, envelope.EventName, envelope.EventId, Name, attempt);
                    return new ConsumeOutcome(ConsumeAction.DeadLetter, attempt, Error: exception, Reason: "retries-exhausted");
                }

                TimeSpan delay = _options.RetryDelays[Math.Min(attempt - 1, _options.RetryDelays.Count - 1)];
                LogRetry(exception, envelope.EventName, envelope.EventId, Name, attempt, delay);
                return new ConsumeOutcome(ConsumeAction.Retry, attempt, delay, exception, "retry");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{EventName} ({EventId}) {Consumer} tüketicisinde {Attempt}. denemede işlenemedi; {Delay} sonra tekrar denenecek.")]
    private partial void LogRetry(Exception exception, string eventName, Guid eventId, string consumer, int attempt, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "{EventName} ({EventId}) {Consumer} tüketicisinde {Attempt} denemede işlenemedi; DLQ'ya gönderiliyor.")]
    private partial void LogExhausted(Exception exception, string eventName, Guid eventId, string consumer, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "{EventName} ({EventId}) {Consumer} tüketicisinde kalıcı hata; yeniden denenmeden DLQ'ya gönderiliyor.")]
    private partial void LogPermanent(Exception exception, string eventName, Guid eventId, string consumer);
}
