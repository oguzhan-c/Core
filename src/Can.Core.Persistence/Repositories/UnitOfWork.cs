using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Can.Core.Persistence.Repositories;

/// <summary><see cref="IUnitOfWork"/>'ün EF Core implementasyonu (scoped; DI scope'undaki DbContext'i kullanır).</summary>
public sealed class UnitOfWork : IUnitOfWork, IAsyncDisposable
{
    private readonly DbContext _context;
    private IDbContextTransaction? _transaction;

    public UnitOfWork(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public bool HasActiveTransaction => _transaction is not null;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _context.SaveChangesAsync(cancellationToken);

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("Zaten açık bir transaction var.");

        _transaction = await _context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        IDbContextTransaction transaction =
            _transaction ?? throw new InvalidOperationException("Commit edilecek bir transaction yok.");

        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeTransactionAsync().ConfigureAwait(false);
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
            return;

        try
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeTransactionAsync().ConfigureAwait(false);

            // Geri alınan değişiklikler change tracker'da kalmasın.
            _context.ChangeTracker.Clear();
        }
    }

    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // İç içe çağrı: dıştaki transaction'a katıl.
        if (HasActiveTransaction)
        {
            TResult nested = await operation(cancellationToken).ConfigureAwait(false);
            await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return nested;
        }

        IExecutionStrategy strategy = _context.Database.CreateExecutionStrategy();

        return await strategy
            .ExecuteAsync(
                async token =>
                {
                    await BeginTransactionAsync(token).ConfigureAwait(false);
                    try
                    {
                        TResult result = await operation(token).ConfigureAwait(false);
                        await SaveChangesAsync(token).ConfigureAwait(false);
                        await CommitTransactionAsync(token).ConfigureAwait(false);
                        return result;
                    }
                    catch
                    {
                        await RollbackTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => DisposeTransactionAsync();

    private async ValueTask DisposeTransactionAsync()
    {
        if (_transaction is null)
            return;

        await _transaction.DisposeAsync().ConfigureAwait(false);
        _transaction = null;
    }
}
