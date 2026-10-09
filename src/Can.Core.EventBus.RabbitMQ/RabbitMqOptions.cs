using System.Globalization;

namespace Can.Core.EventBus.RabbitMQ;

/// <summary>
/// RabbitMQ taşıyıcısı. Yayın: tüm event'ler tek topic exchange'e, routing key = event adı. Tüketim: servis başına
/// bir quorum kuyruk (<see cref="EventBrokerOptions.ConsumerName"/>), dinlenen event adlarıyla bağlanır.
/// Gecikmeli deneme: <c>{kuyruk}.retry.{saniye}s</c> kuyrukları (TTL dolunca ana kuyruğa döner), DLQ: <c>{kuyruk}.dlq</c>.
/// </summary>
public sealed class RabbitMqOptions : EventBrokerOptions
{
    /// <summary>AMQP adresi (<c>amqp://kullanıcı:şifre@host:5672/vhost</c>, TLS için <c>amqps://</c>). Şifreyi user-secrets'a yaz.</summary>
    public string ConnectionString { get; set; } = "amqp://guest:guest@localhost:5672/";

    /// <summary>Event'lerin yayınlandığı topic exchange.</summary>
    public string Exchange { get; set; } = "can.events";

    /// <summary>Kuyruk tipi: <c>quorum</c> (önerilen; çoğaltılmış, kalıcı) ya da <c>classic</c>.</summary>
    public string QueueType { get; set; } = "quorum";

    /// <summary>Onaylanmamış en fazla mesaj (kanal başına).</summary>
    public ushort PrefetchCount { get; set; } = 50;

    /// <summary>Aynı anda işlenen mesaj (istemcinin varsayılanı 1: sırayla).</summary>
    public ushort ConsumerConcurrency { get; set; } = 8;

    /// <summary>Exchange, kuyruk ve bağlamaları açılışta oluştur (yetki yoksa kapat; altyapı ekibi kurar).</summary>
    public bool DeclareTopology { get; set; } = true;

    /// <summary>Yönetim arayüzünde görünen bağlantı adı (boşsa tüketici adı).</summary>
    public string? ClientName { get; set; }

    internal string Queue => ResolveConsumerName();

    internal string DeadLetterQueue => $"{Queue}.dlq";

    internal string RetryQueue(TimeSpan delay) =>
        $"{Queue}.retry.{((long)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture)}s";

    internal IEnumerable<TimeSpan> DistinctDelays => RetryDelays.Distinct();
}
