using Can.Core.Application.Rules;
using Can.Core.Domain.Exceptions;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;

namespace Can.Core.Application.Tests;

// ------------------------------------------------------------ Sahte altyapı

public sealed class FakeCurrentUser : ICurrentUser
{
    public string? Id { get; set; } = "user-1";
    public string? UserName => Id;
    public string? Email => null;
    public List<string> RoleList { get; } = [];
    public IReadOnlyCollection<string> Roles => RoleList;
    public List<string> PermissionList { get; } = [];
    public IReadOnlyCollection<string> Permissions => PermissionList;
}

public sealed class FakeCurrentTenant : ICurrentTenant
{
    public object? Id { get; set; }
}

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int TransactionCount { get; private set; }
    public bool RolledBack { get; private set; }
    public bool HasActiveTransaction => false;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    public Task BeginTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        TransactionCount++;
        try
        {
            return await operation(cancellationToken);
        }
        catch
        {
            RolledBack = true;
            throw;
        }
    }
}

public sealed class Counter
{
    private int _value;

    public int Value => _value;

    public int Increment() => Interlocked.Increment(ref _value);
}

// ------------------------------------------------------------ Command: tüm işaretleyiciler

public sealed record CreateProductCommand(string Name, int Price)
    : IRequest<int>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest, ILoggableRequest
{
    public IReadOnlyCollection<string> Roles => ["Product.Write"];
    public IReadOnlyCollection<string> Permissions => ["products.create"];
    public IReadOnlyCollection<string> CacheTagsToRemove => ["products"];
}

public sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty();
        RuleFor(c => c.Price).GreaterThan(0);
    }
}

public sealed class CreateProductCommandHandler(Counter counter) : IRequestHandler<CreateProductCommand, int>
{
    public Task<int> Handle(CreateProductCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(counter.Increment());
}

// ------------------------------------------------------------ Query: önbellekli

public sealed record GetProductsQuery(int Page, bool Fresh = false) : IRequest<List<string>>, ICachableRequest
{
    public string CacheKey => $"products:{Page}";
    public IReadOnlyCollection<string> CacheTags => ["products"];
    public bool BypassCache => Fresh;
}

public sealed class GetProductsQueryHandler(Counter counter) : IRequestHandler<GetProductsQuery, List<string>>
{
    public Task<List<string>> Handle(GetProductsQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(new List<string> { $"call-{counter.Increment()}" });
}

// ------------------------------------------------------------ Hata veren transactional command

public sealed record FailingCommand : IRequest, ITransactionalRequest;

public sealed class FailingCommandHandler : IRequestHandler<FailingCommand>
{
    public Task Handle(FailingCommand request, CancellationToken cancellationToken) =>
        throw new BusinessException("kural ihlali");
}

// ------------------------------------------------------------ Business rules

public sealed class ProductBusinessRules(Counter counter) : BaseBusinessRules
{
    public int Calls => counter.Value;
}

// ------------------------------------------------------------ Result döndüren istekler

public sealed record CreateOrderCommand(string Name, bool Reject = false)
    : IRequest<Result<int>>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest, ILoggableRequest
{
    public IReadOnlyCollection<string> Roles => ["Order.Write"];
    public IReadOnlyCollection<string> CacheTagsToRemove => ["orders"];
}

public sealed class CreateOrderCommandValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty();
    }
}

public sealed class CreateOrderCommandHandler(Counter counter) : IRequestHandler<CreateOrderCommand, Result<int>>
{
    public static readonly Error Rejected = Error.Failure("order.rejected", "Sipariş reddedildi.");

    public Task<Result<int>> Handle(CreateOrderCommand request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<int>>(request.Reject ? Rejected : counter.Increment());
}

public sealed record GetOrdersQuery(int Page, bool Missing = false) : IRequest<Result<List<string>>>, ICachableRequest
{
    public string CacheKey => $"orders:{Page}:{Missing}";
    public IReadOnlyCollection<string> CacheTags => ["orders"];
}

public sealed class GetOrdersQueryHandler(Counter counter) : IRequestHandler<GetOrdersQuery, Result<List<string>>>
{
    public static readonly Error NotFound = Error.NotFound("orders.not_found", "Sipariş yok.");

    public Task<Result<List<string>>> Handle(GetOrdersQuery request, CancellationToken cancellationToken)
    {
        int call = counter.Increment();
        return Task.FromResult<Result<List<string>>>(request.Missing ? NotFound : new List<string> { $"call-{call}" });
    }
}
