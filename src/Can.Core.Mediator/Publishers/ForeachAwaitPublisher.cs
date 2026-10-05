namespace Can.Core.Mediator.Publishers;

/// <summary>
/// Varsayılan strateji: handler'lar sırayla, birbirini bekleyerek çalışır.
/// Bir handler hata fırlatırsa sonrakiler çalışmaz ve hata çağırana iletilir.
/// Domain event'ler için en güvenli seçenektir (aynı DbContext'i paylaşan handler'lar çakışmaz).
/// </summary>
public sealed class ForeachAwaitPublisher : INotificationPublisher
{
    public async Task Publish(
        IReadOnlyList<NotificationHandlerExecutor> handlerExecutors,
        object notification,
        CancellationToken cancellationToken)
    {
        foreach (NotificationHandlerExecutor executor in handlerExecutors)
            await executor.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
    }
}
