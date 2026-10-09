using Can.Core.BackgroundJobs;
using Can.Core.EventBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Inbox;

/// <summary>
/// EF Core inbox: handler'lar ve inbox kaydı aynı transaction'da çalışır. Handler'lar aynı (scoped)
/// <typeparamref name="TContext"/>'i kullanıyorsa veritabanı değişiklikleri "tam bir kez" uygulanır: işlem yarıda
/// kalırsa ikisi birden geri alınır, tekrar gelen teslim kayıt sayesinde atlanır. Eşzamanlı iki teslimde ikincinin
/// kaydı birincil anahtara takılır, transaction'ı geri alınır ve sonraki denemede "işlenmiş" görülür.
/// </summary>
/// <remarks>
/// Veritabanı dışı yan etkiler (e-posta, HTTP çağrısı) transaction'a girmez; onları idempotent yaz ya da outbox ile
/// ayrı bir event'e çevir. Yeniden deneme stratejisi (<c>EnableRetryOnFailure</c>) varsa geçici hatalarda handler'lar
/// yeniden çalıştırılır.
/// </remarks>
public sealed class EfInboxStore<TContext> : IInboxStore
    where TContext : DbContext
{
    private readonly TimeProvider _time;

    public EfInboxStore(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    public async Task<bool> ExecuteOnceAsync(IServiceProvider services, string consumer, EventEnvelope envelope, Func<CancellationToken, Task> handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(handle);

        TContext db = services.GetRequiredService<TContext>();

        if (db.Database.CurrentTransaction is not null)
            return await RunAsync(db, consumer, envelope, handle, cancellationToken).ConfigureAwait(false); // dış transaction'a katıl

        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async ct =>
            {
                db.ChangeTracker.Clear(); // geçici hatadan sonra yeniden denemede temiz başla
                await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                bool executed = await RunAsync(db, consumer, envelope, handle, ct).ConfigureAwait(false);
                if (executed)
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                return executed;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RunAsync(TContext db, string consumer, EventEnvelope envelope, Func<CancellationToken, Task> handle, CancellationToken ct)
    {
        DbSet<InboxMessage> inbox = db.Set<InboxMessage>();
        Guid id = envelope.EventId;
        if (await inbox.AsNoTracking().AnyAsync(m => m.Consumer == consumer && m.EventId == id, ct).ConfigureAwait(false))
            return false;

        await handle(ct).ConfigureAwait(false);

        inbox.Add(new InboxMessage
        {
            Consumer = consumer,
            EventId = id,
            EventName = envelope.EventName.Length > 512 ? envelope.EventName[..512] : envelope.EventName,
            ProcessedAt = _time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Süresi dolan inbox kayıtlarını siler.</summary>
public sealed class InboxCleanupJob<TContext> : IBackgroundJob
    where TContext : DbContext
{
    private readonly TContext _db;
    private readonly InboxOptions _options;
    private readonly TimeProvider _time;

    public InboxCleanupJob(TContext db, InboxOptions options, TimeProvider? timeProvider = null)
    {
        _db = db;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        DateTime cutoff = (_time.GetUtcNow() - _options.Retention).UtcDateTime;
        await _db.Set<InboxMessage>().Where(m => m.ProcessedAt < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
