using Azure.Core;

namespace Can.Core.EventBus.AzureServiceBus;

/// <summary>
/// Azure Service Bus taşıyıcısı. Tüm event'ler tek topic'e (<see cref="TopicName"/>), <c>Subject</c> = event adı.
/// Her servis bir subscription (<see cref="EventBrokerOptions.ConsumerName"/>); yalnızca dinlediği event'ler correlation
/// filtreleriyle gelir. Gecikmeli deneme: mesaj zamanlanmış olarak topic'e yeniden gönderilir (<c>Subject = can.retry</c>,
/// yalnızca bu subscription'ın kuralı alır). DLQ: subscription'ın yerleşik dead-letter kuyruğu.
/// </summary>
public sealed class AzureServiceBusOptions : EventBrokerOptions
{
    public const string RetrySubject = "can.retry";

    /// <summary>Bağlantı dizesi (emülatör: <c>Endpoint=sb://localhost;...;UseDevelopmentEmulator=true;</c>). User-secrets'a yaz.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Kimlikle bağlanmak için ad alanı (<c>ornek.servicebus.windows.net</c>) + <see cref="Credential"/> (ör. <c>DefaultAzureCredential</c>).</summary>
    public string? FullyQualifiedNamespace { get; set; }

    public TokenCredential? Credential { get; set; }

    public string TopicName { get; set; } = "can-events";

    /// <summary>Aynı anda işlenen mesaj (SDK varsayılanı 1).</summary>
    public int MaxConcurrentCalls { get; set; } = 8;

    public int PrefetchCount { get; set; }

    /// <summary>Uzun süren handler'larda mesaj kilidi bu süreye kadar kendiliğinden yenilenir.</summary>
    public TimeSpan MaxAutoLockRenewalDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Topic, subscription ve kuralları oluştur/eşitle (Manage yetkisi ister; kapalıysa altyapı kurar).</summary>
    public bool ManageTopology { get; set; } = true;

    /// <summary>Service Bus'ın kendi teslim sınırı (son güvenlik ağı; uygulama hataları <c>RetryDelays</c> ile yönetilir).</summary>
    public int MaxDeliveryCount { get; set; } = 10;

    internal string Subscription => ResolveConsumerName();
}
