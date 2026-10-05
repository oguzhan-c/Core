using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Mediator.Internal;

// Mediator, istek tipini derleme anında bilmez (elinde IRequest<TResponse> vardır).
// Her istek tipi için bir kez, reflection ile kapalı generic bir wrapper oluşturulur ve cache'lenir;
// sonraki çağrılarda reflection kullanılmaz, her şey tip güvenli generic kodla çalışır.

internal abstract class RequestHandlerBase
{
    public abstract Task<object?> HandleAsObject(object request, IServiceProvider serviceProvider, CancellationToken cancellationToken);
}

internal abstract class RequestHandlerWrapper<TResponse> : RequestHandlerBase
{
    public abstract Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override async Task<object?> HandleAsObject(
        object request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken) =>
        await Handle((IRequest<TResponse>)request, serviceProvider, cancellationToken).ConfigureAwait(false);

    public override Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;

        IRequestHandler<TRequest, TResponse> handler =
            serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
            ?? throw new HandlerNotFoundException(typeof(TRequest), typeof(IRequestHandler<TRequest, TResponse>));

        RequestHandlerDelegate<TResponse> next = () => handler.Handle(typedRequest, cancellationToken);

        // Behavior'lar kayıt sırasıyla gelir; ilk kaydedilen en dışta çalışsın diye tersten sarılır.
        IPipelineBehavior<TRequest, TResponse>[] behaviors = serviceProvider
            .GetServices<IPipelineBehavior<TRequest, TResponse>>()
            .ToArray();

        for (int i = behaviors.Length - 1; i >= 0; i--)
        {
            IPipelineBehavior<TRequest, TResponse> behavior = behaviors[i];
            RequestHandlerDelegate<TResponse> inner = next;
            next = () => behavior.Handle(typedRequest, inner, cancellationToken);
        }

        return next();
    }
}
