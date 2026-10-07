using System.Collections.Concurrent;
using System.Diagnostics;
using Can.Core.Mediator.Internal;

namespace Can.Core.Mediator;

/// <summary>
/// Varsayılan <see cref="IMediator"/> implementasyonu.
/// </summary>
/// <remarks>
/// Her istek/bildirim tipi için wrapper'lar yalnızca ilk kullanımda reflection ile oluşturulur
/// ve uygulama ömrü boyunca cache'lenir. Mediator'ın kendisi hafiftir; DI scope'unu taşır.
/// </remarks>
public class Mediator : IMediator
{
    private static readonly ConcurrentDictionary<Type, RequestHandlerBase> RequestHandlers = new();
    private static readonly ConcurrentDictionary<Type, object> StreamHandlers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapper[]> NotificationHandlers = new();

    private readonly IServiceProvider _serviceProvider;
    private readonly INotificationPublisher _publisher;

    public Mediator(IServiceProvider serviceProvider, INotificationPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(publisher);

        _serviceProvider = serviceProvider;
        _publisher = publisher;
    }

    // ---------------------------------------------------------------- Send

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)RequestHandlers.GetOrAdd(
            request.GetType(),
            static requestType => CreateRequestWrapper(requestType, typeof(TResponse))
        );

        return wrapper.Handle(request, _serviceProvider, cancellationToken);
    }

    public Task Send(IRequest request, CancellationToken cancellationToken = default) =>
        Send<Unit>(request, cancellationToken);

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequestHandlerBase wrapper = RequestHandlers.GetOrAdd(
            request.GetType(),
            static requestType =>
            {
                Type requestInterface =
                    requestType
                        .GetInterfaces()
                        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
                    ?? throw new ArgumentException(
                        $"'{requestType.FullName}' bir istek değil; IRequest veya IRequest<TResponse> uygulamalı.",
                        "request"
                    );

                return CreateRequestWrapper(requestType, requestInterface.GetGenericArguments()[0]);
            }
        );

        return wrapper.HandleAsObject(request, _serviceProvider, cancellationToken);
    }

    private static RequestHandlerBase CreateRequestWrapper(Type requestType, Type responseType) =>
        (RequestHandlerBase)Activator.CreateInstance(
            typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(requestType, responseType)
        )!;

    // ---------------------------------------------------------------- Stream

    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (StreamHandlerWrapper<TResponse>)StreamHandlers.GetOrAdd(
            request.GetType(),
            static requestType =>
                Activator.CreateInstance(
                    typeof(StreamHandlerWrapperImpl<,>).MakeGenericType(requestType, typeof(TResponse))
                )!
        );

        return wrapper.Handle(request, _serviceProvider, cancellationToken);
    }

    // ---------------------------------------------------------------- Publish

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : notnull => Publish((object)notification, cancellationToken);

    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        NotificationHandlerWrapper[] wrappers = NotificationHandlers.GetOrAdd(
            notification.GetType(),
            static notificationType =>
                GetHandledTypes(notificationType)
                    .Select(handledType =>
                        (NotificationHandlerWrapper)Activator.CreateInstance(
                            typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(handledType)
                        )!
                    )
                    .ToArray()
        );

        var executors = new List<NotificationHandlerExecutor>();
        foreach (NotificationHandlerWrapper wrapper in wrappers)
            wrapper.CollectExecutors(_serviceProvider, executors);

        if (executors.Count == 0)
            return Task.CompletedTask;

        return MediatorTelemetry.Source.HasListeners()
            ? PublishWithActivityAsync(executors, notification, cancellationToken)
            : _publisher.Publish(executors, notification, cancellationToken);
    }

    // Ayrı async metot: span (Activity.Current) çağırana sızmasın.
    private async Task PublishWithActivityAsync(List<NotificationHandlerExecutor> executors, object notification, CancellationToken cancellationToken)
    {
        using Activity? activity = MediatorTelemetry.Source.StartActivity($"publish {notification.GetType().Name}");
        activity?.SetTag("can.event", notification.GetType().Name);
        activity?.SetTag("can.handlers", executors.Count);

        try
        {
            await _publisher.Publish(executors, notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }

    /// <summary>
    /// Bildirimin kendisi, base sınıfları (object hariç) ve tüm arayüzleri.
    /// Sıra: önce en özel tip, sonra base sınıflar, en son arayüzler.
    /// </summary>
    internal static IEnumerable<Type> GetHandledTypes(Type notificationType)
    {
        for (Type? type = notificationType; type is not null && type != typeof(object); type = type.BaseType)
            yield return type;

        foreach (Type @interface in notificationType.GetInterfaces())
            yield return @interface;
    }

    /// <summary>Testler için: tüm wrapper cache'lerini temizler.</summary>
    internal static void ClearCaches()
    {
        RequestHandlers.Clear();
        StreamHandlers.Clear();
        NotificationHandlers.Clear();
    }
}
