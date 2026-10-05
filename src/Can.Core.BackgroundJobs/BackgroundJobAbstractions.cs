namespace Can.Core.BackgroundJobs;

/// <summary>Parametresiz arka plan işi (kuyruğa atılabilir ya da tekrarlayan iş olarak çalıştırılabilir).</summary>
/// <remarks>Her çalıştırma kendi DI scope'unda yapılır; DbContext, repository gibi scoped servisler enjekte edilebilir.</remarks>
public interface IBackgroundJob
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>Parametreli arka plan işi.</summary>
/// <example>
/// <code>
/// public sealed record WelcomeEmailArgs(Guid UserId);
///
/// public sealed class SendWelcomeEmailJob(IEmailSender sender, AppDbContext db) : IBackgroundJob&lt;WelcomeEmailArgs&gt;
/// {
///     public async Task ExecuteAsync(WelcomeEmailArgs args, CancellationToken ct) { ... }
/// }
///
/// await queue.EnqueueAsync&lt;SendWelcomeEmailJob, WelcomeEmailArgs&gt;(new(user.Id));
/// </code>
/// </example>
public interface IBackgroundJob<in TArgs>
{
    Task ExecuteAsync(TArgs args, CancellationToken cancellationToken);
}

/// <summary>
/// İşi hemen dönüp arka planda çalıştırmak için kuyruk. İş, kuyruğa atıldığı andaki tenant adına çalışır.
/// </summary>
/// <remarks>
/// Kuyruk bellektedir: uygulama kapanırsa bekleyen işler kaybolur. Kaybolmaması gereken işler için
/// <c>IIntegrationEvent</c> + outbox kullan (event veritabanına aynı transaction'da yazılır).
/// </remarks>
public interface IBackgroundJobQueue
{
    ValueTask EnqueueAsync<TJob>(CancellationToken cancellationToken = default)
        where TJob : class, IBackgroundJob;

    ValueTask EnqueueAsync<TJob, TArgs>(TArgs args, CancellationToken cancellationToken = default)
        where TJob : class, IBackgroundJob<TArgs>;
}
