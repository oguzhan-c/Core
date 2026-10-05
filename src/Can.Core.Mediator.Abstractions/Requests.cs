namespace Can.Core.Mediator;

/// <summary>Tüm istekler için işaretleyici (pipeline behavior kısıtlarında kullanışlıdır).</summary>
public interface IBaseRequest { }

/// <summary>Yanıt olarak <typeparamref name="TResponse"/> döndüren istek (command ya da query).</summary>
public interface IRequest<out TResponse> : IBaseRequest { }

/// <summary>Yanıt döndürmeyen istek.</summary>
public interface IRequest : IRequest<Unit> { }

/// <summary><typeparamref name="TRequest"/> isteğini işleyen handler. Her istek tipi için tek handler olur.</summary>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Yanıt döndürmeyen istekler için handler. <see cref="Unit"/> ile uğraşmadan
/// sadece <see cref="Task"/> döndürmen yeterli.
/// </summary>
public interface IRequestHandler<in TRequest> : IRequestHandler<TRequest, Unit>
    where TRequest : IRequest<Unit>
{
    new Task Handle(TRequest request, CancellationToken cancellationToken);

    async Task<Unit> IRequestHandler<TRequest, Unit>.Handle(TRequest request, CancellationToken cancellationToken)
    {
        await Handle(request, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
