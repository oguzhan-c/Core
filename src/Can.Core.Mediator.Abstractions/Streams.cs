namespace Can.Core.Mediator;

/// <summary>Sonucu <see cref="IAsyncEnumerable{T}"/> olarak akıtan istek (büyük listeler, export vb.).</summary>
public interface IStreamRequest<out TResponse> { }

/// <summary><typeparamref name="TRequest"/> stream isteğini işleyen handler.</summary>
public interface IStreamRequestHandler<in TRequest, TResponse>
    where TRequest : IStreamRequest<TResponse>
{
    IAsyncEnumerable<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Stream zincirindeki bir sonraki adım.</summary>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>();

/// <summary>Stream istekleri için pipeline behavior.</summary>
public interface IStreamPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
