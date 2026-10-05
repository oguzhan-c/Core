namespace Can.Core.Mediator;

/// <summary>
/// Bildirimler için isteğe bağlı işaretleyici. Zorunlu DEĞİLDİR: <see cref="INotificationHandler{TNotification}"/>
/// her tiple çalışır. Böylece Domain katmanındaki event'ler (ör. <c>IDomainEvent</c>) mediator'a
/// bağımlı olmadan doğrudan yayınlanabilir.
/// </summary>
public interface INotification { }

/// <summary>
/// <typeparamref name="TNotification"/> bildirimini işleyen handler. Bir bildirimin sıfır ya da
/// daha fazla handler'ı olabilir.
/// </summary>
/// <remarks>
/// Handler'lar tip hiyerarşisine göre çalışır: <c>INotificationHandler&lt;IDomainEvent&gt;</c>
/// tüm domain event'leri, <c>INotificationHandler&lt;OrderConfirmed&gt;</c> sadece o olayı yakalar.
/// </remarks>
public interface INotificationHandler<in TNotification>
    where TNotification : notnull
{
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}

/// <summary>Tek bir handler'ı çalıştırmak için gerekenler; <see cref="INotificationPublisher"/>'a verilir.</summary>
/// <param name="HandlerInstance">Handler nesnesi (loglama vb. için).</param>
/// <param name="HandlerCallback">Handler'ı çağıran delegate.</param>
public readonly record struct NotificationHandlerExecutor(
    object HandlerInstance,
    Func<object, CancellationToken, Task> HandlerCallback);

/// <summary>
/// Bir bildirimin handler'larının nasıl çalıştırılacağını belirleyen strateji
/// (sırayla, paralel, arka planda ...).
/// </summary>
public interface INotificationPublisher
{
    Task Publish(
        IReadOnlyList<NotificationHandlerExecutor> handlerExecutors,
        object notification,
        CancellationToken cancellationToken);
}
