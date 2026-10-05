using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Mediator.Internal;

// Bir bildirim, kendi tipi + base sınıfları + arayüzleri için kayıtlı tüm handler'lara gider.
// Her "handle edilen tip" için bir wrapper vardır; wrapper o tipin handler'larını DI'dan çözer.

internal abstract class NotificationHandlerWrapper
{
    public abstract void CollectExecutors(
        IServiceProvider serviceProvider,
        List<NotificationHandlerExecutor> executors);
}

internal sealed class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapper
    where TNotification : notnull
{
    public override void CollectExecutors(
        IServiceProvider serviceProvider,
        List<NotificationHandlerExecutor> executors)
    {
        foreach (INotificationHandler<TNotification> handler in serviceProvider.GetServices<INotificationHandler<TNotification>>())
        {
            executors.Add(
                new NotificationHandlerExecutor(
                    handler,
                    (notification, cancellationToken) => handler.Handle((TNotification)notification, cancellationToken)
                )
            );
        }
    }
}
