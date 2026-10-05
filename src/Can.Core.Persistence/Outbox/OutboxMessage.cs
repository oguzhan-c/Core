using System.Text.Json;
using Can.Core.Domain.Events;

namespace Can.Core.Persistence.Outbox;

/// <summary>
/// Outbox tablosundaki bir kayıt: aggregate değişiklikleriyle aynı transaction'da yazılan, arka planda
/// yayınlanmayı bekleyen <see cref="IIntegrationEvent"/>. Tarihler UTC'dir.
/// </summary>
public sealed class OutboxMessage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private OutboxMessage()
    {
        Type = string.Empty;
        Payload = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>Event'in kendi kimliği (<see cref="IDomainEvent.EventId"/>); handler'larda tekrarları ayıklamak için.</summary>
    public Guid EventId { get; private set; }

    /// <summary>Event tipinin adı (<c>Namespace.Tip, Assembly</c>).</summary>
    public string Type { get; private set; }

    /// <summary>Event'in JSON hâli.</summary>
    public string Payload { get; private set; }

    /// <summary>Event'in oluştuğu tenant; yayınlarken handler'lar bu tenant adına çalışır.</summary>
    public string? TenantId { get; private set; }

    public DateTime OccurredAt { get; private set; }

    /// <summary>Başarıyla yayınlandığı an; boşsa bekliyor.</summary>
    public DateTime? ProcessedAt { get; private set; }

    /// <summary>Bir işlemci mesajı bu ana kadar kilitledi (birden fazla uygulama örneği aynı mesajı almasın).</summary>
    public DateTime? LockedUntil { get; private set; }

    /// <summary>Başarısız yayınlama denemesi sayısı.</summary>
    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    internal static OutboxMessage Create(IIntegrationEvent integrationEvent, string? tenantId)
    {
        Type type = integrationEvent.GetType();

        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventId = integrationEvent.EventId,
            Type = GetTypeName(type),
            Payload = JsonSerializer.Serialize(integrationEvent, type, JsonOptions),
            TenantId = tenantId,
            OccurredAt = integrationEvent.OccurredAt.UtcDateTime,
        };
    }

    /// <summary>Kaydedilen event'i geri oluşturur. Yalnızca <see cref="IIntegrationEvent"/> tipleri kabul edilir.</summary>
    public IIntegrationEvent Deserialize()
    {
        Type type =
            System.Type.GetType(Type, throwOnError: false)
            ?? throw new InvalidOperationException($"Outbox event tipi bulunamadı: '{Type}'.");

        if (!typeof(IIntegrationEvent).IsAssignableFrom(type))
            throw new InvalidOperationException($"'{Type}' bir IIntegrationEvent değil; outbox'tan yayınlanamaz.");

        return (IIntegrationEvent)(
            JsonSerializer.Deserialize(Payload, type, JsonOptions)
            ?? throw new InvalidOperationException($"Outbox mesajı ({Id}) boş.")
        );
    }

    internal static string GetTypeName(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";
}
