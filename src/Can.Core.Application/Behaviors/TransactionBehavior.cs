using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;

namespace Can.Core.Application.Behaviors;

/// <summary>
/// <see cref="ITransactionalRequest"/> handler'ını transaction içinde çalıştırır: handler bittiğinde
/// değişiklikler kaydedilir ve commit edilir; hata olursa her şey geri alınır. Bu isteklerin handler'larında
/// <c>SaveChangesAsync</c> çağırmaya gerek yoktur. Domain event handler'larının değişiklikleri de aynı transaction'a dahildir.
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ITransactionalRequest
{
    private readonly IUnitOfWork _unitOfWork;

    public TransactionBehavior(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(_ => next(), cancellationToken);
}
