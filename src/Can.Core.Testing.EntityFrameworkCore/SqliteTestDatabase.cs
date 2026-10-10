using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Testing.EntityFrameworkCore;

/// <summary>
/// Bellekte SQLite veritabanı: bağlantı açık kaldıkça yaşar, test bitince (dispose) yok olur. EF'in InMemory
/// sağlayıcısının aksine ilişkiler, benzersiz indeksler, transaction'lar ve SQL çevirisi gerçektir.
/// </summary>
/// <remarks>
/// SQLite <c>decimal</c> toplayamaz ve <c>DateTimeOffset</c> karşılaştıramaz: bu tür sorgular üretimde (PostgreSQL)
/// çalışıp burada hata verebilir; o testleri gerçek veritabanına karşı (Testcontainers vb.) çalıştır.
/// </remarks>
/// <example>
/// <code>
/// await using var database = new SqliteTestDatabase();
/// services.AddDbContext&lt;AppDbContext&gt;(database.Configure);
/// ServiceProvider provider = services.BuildServiceProvider();
/// await database.CreateSchemaAsync&lt;AppDbContext&gt;(provider);
/// </code>
/// </example>
public sealed class SqliteTestDatabase : IAsyncDisposable, IDisposable
{
    public SqliteTestDatabase()
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
    }

    /// <summary>Açık bağlantı (kapanırsa veritabanı silinir).</summary>
    public SqliteConnection Connection { get; }

    /// <summary><c>AddDbContext</c> için: bu bağlantıyı kullanır, hassas veri ve ayrıntılı hatalar açık.</summary>
    public void Configure(DbContextOptionsBuilder options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UseSqlite(Connection).EnableSensitiveDataLogging().EnableDetailedErrors();
    }

    /// <summary>Şemayı oluşturur (migration'sız, modelden).</summary>
    public async Task CreateSchemaAsync<TContext>(IServiceProvider services, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tüm veriyi siler ve şemayı yeniden kurar (aynı veritabanını testler arasında paylaşırken).</summary>
    public async Task ResetAsync<TContext>(IServiceProvider services, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        TContext context = scope.ServiceProvider.GetRequiredService<TContext>();
        await context.Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Yeni bir scope'ta DbContext ile iş yapar (kayıt hazırlamak / sonucu doğrulamak için; izleme önbelleği taşınmaz).</summary>
    public static async Task<TResult> InScopeAsync<TContext, TResult>(IServiceProvider services, Func<TContext, Task<TResult>> work)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(work);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TContext>()).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();

    public void Dispose() => Connection.Dispose();
}
