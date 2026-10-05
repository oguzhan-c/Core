namespace Can.Core.Mediator.Publishers;

/// <summary>
/// Handler'ları paralel başlatır ve hepsinin bitmesini bekler. Hataların tümü
/// <see cref="AggregateException"/> içinde toplanır (await ilkini fırlatır).
/// </summary>
/// <remarks>
/// Dikkat: aynı scoped DbContext'i kullanan handler'lar paralel çalışırsa EF Core hata verir.
/// Bu stratejiyi birbirinden bağımsız (e-posta, cache, log) handler'lar için kullan.
/// </remarks>
public sealed class TaskWhenAllPublisher : INotificationPublisher
{
    public Task Publish(
        IReadOnlyList<NotificationHandlerExecutor> handlerExecutors,
        object notification,
        CancellationToken cancellationToken)
    {
        var tasks = new Task[handlerExecutors.Count];

        for (int i = 0; i < handlerExecutors.Count; i++)
            tasks[i] = handlerExecutors[i].HandlerCallback(notification, cancellationToken);

        return Task.WhenAll(tasks);
    }
}
