namespace Can.Core.Domain.Events;

/// <summary>
/// Kalıcı (outbox üzerinden) yayınlanması gereken domain event. Bu arayüzü uygulayan event'ler kayıttan hemen
/// sonra bellekte yayınlanmaz; aggregate değişiklikleriyle AYNI transaction içinde outbox tablosuna yazılır ve
/// arka plan işi tarafından yayınlanır. Uygulama kayıttan hemen sonra çökse bile event kaybolmaz.
/// </summary>
/// <example>
/// <code>
/// public sealed record OrderShipped(int OrderId, string Email) : DomainEvent, IIntegrationEvent;
/// </code>
/// </example>
/// <remarks>
/// Teslimat "en az bir kez"dir: hata durumunda handler tekrar çalışabilir. Handler'ları idempotent yaz
/// (gerekirse <see cref="IDomainEvent.EventId"/> ile tekrarları ayıkla). Event JSON olarak saklanır; bu yüzden
/// basit, serileştirilebilir tipler (id, metin, sayı) taşımalı, entity referansı taşımamalı.
/// </remarks>
public interface IIntegrationEvent : IDomainEvent;
