namespace Can.Core.Mediator;

/// <summary>İstekleri (command/query/stream) tek handler'ına gönderir.</summary>
public interface ISender
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);

    Task Send(IRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tipi derleme anında bilinmeyen istekler için. Yanıtı döndürür (yanıtsız isteklerde <see cref="Unit"/>).</summary>
    Task<object?> Send(object request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TResponse> CreateStream<TResponse>(
        IStreamRequest<TResponse> request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bildirimleri (ve domain event'leri) tüm handler'larına yayınlar. Handler'lar her zaman
/// nesnenin <b>gerçek (runtime) tipine</b> ve onun base tiplerine/arayüzlerine göre bulunur;
/// yani <c>Publish((IDomainEvent)evt)</c> çağrısı da <c>OrderConfirmed</c> handler'larını çalıştırır.
/// </summary>
public interface IPublisher
{
    Task Publish(object notification, CancellationToken cancellationToken = default);

    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : notnull;
}

/// <summary><see cref="ISender"/> + <see cref="IPublisher"/>.</summary>
public interface IMediator : ISender, IPublisher { }
