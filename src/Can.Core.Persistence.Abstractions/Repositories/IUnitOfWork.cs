namespace Can.Core.Persistence.Repositories;

/// <summary>
/// Bir iş biriminin değişikliklerini tek seferde kaydeder. Repository'ler <c>SaveChanges</c>
/// çağırmaz; handler (ya da transaction behavior) işin sonunda <see cref="SaveChangesAsync"/> çağırır.
/// </summary>
/// <remarks>
/// Kayıt sırasında audit alanları doldurulur, soft delete uygulanır ve kayıt başarılı olursa
/// aggregate'lerin domain event'leri yayınlanır.
/// </remarks>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    bool HasActiveTransaction { get; }

    Task BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task CommitTransactionAsync(CancellationToken cancellationToken = default);

    Task RollbackTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <paramref name="operation"/>'ı bir transaction içinde çalıştırır: başarılıysa kaydedip commit eder,
    /// hata olursa geri alır. Veritabanı sağlayıcısının yeniden deneme stratejisiyle uyumludur.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);
}
