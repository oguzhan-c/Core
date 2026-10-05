using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Mediator.Internal;

internal abstract class StreamHandlerWrapper<TResponse>
{
    public abstract IAsyncEnumerable<TResponse> Handle(
        IStreamRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken);
}

internal sealed class StreamHandlerWrapperImpl<TRequest, TResponse> : StreamHandlerWrapper<TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    public override IAsyncEnumerable<TResponse> Handle(
        IStreamRequest<TResponse> request,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;

        IStreamRequestHandler<TRequest, TResponse> handler =
            serviceProvider.GetService<IStreamRequestHandler<TRequest, TResponse>>()
            ?? throw new HandlerNotFoundException(typeof(TRequest), typeof(IStreamRequestHandler<TRequest, TResponse>));

        StreamHandlerDelegate<TResponse> next = () => handler.Handle(typedRequest, cancellationToken);

        IStreamPipelineBehavior<TRequest, TResponse>[] behaviors = serviceProvider
            .GetServices<IStreamPipelineBehavior<TRequest, TResponse>>()
            .ToArray();

        for (int i = behaviors.Length - 1; i >= 0; i--)
        {
            IStreamPipelineBehavior<TRequest, TResponse> behavior = behaviors[i];
            StreamHandlerDelegate<TResponse> inner = next;
            next = () => behavior.Handle(typedRequest, inner, cancellationToken);
        }

        return next();
    }
}
