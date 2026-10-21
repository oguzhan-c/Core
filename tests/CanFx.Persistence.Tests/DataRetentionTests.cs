using CanFx.Application;
using CanFx.Persistence.Retention;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Persistence.Tests;

public sealed class LogEntry
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class RetentionDbContext(DbContextOptions<RetentionDbContext> options) : DbContext(options)
{
    public DbSet<LogEntry> Entries => Set<LogEntry>();

    // SQLite DateTimeOffset karşılaştıramaz; testte sayıya çevrilir (PostgreSQL/SQL Server'da gerekmez).
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
}

public sealed class DataRetentionTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BusyLock : IDistributedLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string resource, TimeSpan expiry, TimeSpan wait = default, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable?>(null);
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using RetentionDbContext db = new(new DbContextOptionsBuilder<RetentionDbContext>().UseSqlite(_connection).Options);
        await db.Database.EnsureCreatedAsync();

        // 3 eski (40 gün), 2 yeni (5 gün); biri eski ama kapanmamış
        db.Entries.AddRange(
            new LogEntry { Kind = "audit", At = Now.AddDays(-40), ClosedAt = Now.AddDays(-40), CreatedUtc = Now.AddDays(-40).UtcDateTime },
            new LogEntry { Kind = "audit", At = Now.AddDays(-40), ClosedAt = null, CreatedUtc = Now.AddDays(-40).UtcDateTime },
            new LogEntry { Kind = "keep", At = Now.AddDays(-40), ClosedAt = Now.AddDays(-40), CreatedUtc = Now.AddDays(-40).UtcDateTime },
            new LogEntry { Kind = "audit", At = Now.AddDays(-5), ClosedAt = Now.AddDays(-5), CreatedUtc = Now.AddDays(-5).UtcDateTime },
            new LogEntry { Kind = "audit", At = Now.AddDays(-5), ClosedAt = null, CreatedUtc = Now.AddDays(-5).UtcDateTime });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async Task<string[]> RunAsync(Action<DataRetentionOptions> configure, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(new Clock());
        collection.AddDbContext<RetentionDbContext>(o => o.UseSqlite(_connection));
        collection.AddCanDataRetention<RetentionDbContext>(configure);
        services?.Invoke(collection);
        await using ServiceProvider provider = collection.BuildServiceProvider();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DataRetentionJob<RetentionDbContext>>().ExecuteAsync(TestContext.Current.CancellationToken);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            List<LogEntry> left = await scope.ServiceProvider.GetRequiredService<RetentionDbContext>().Entries.OrderBy(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);
            return left.Select(e => $"{e.Kind}:{(Now - e.At).TotalDays:0}:{(e.ClosedAt is null ? "açık" : "kapalı")}").ToArray();
        }
    }

    [Fact]
    public async Task Only_rows_older_than_the_period_and_matching_the_filter_are_deleted()
    {
        string[] left = await RunAsync(o => o.Keep<LogEntry>(TimeSpan.FromDays(30), e => e.At, where: e => e.Kind == "audit"));

        Assert.Equal(new[] { "keep:40:kapalı", "audit:5:kapalı", "audit:5:açık" }, left);
    }

    [Fact]
    public async Task Rows_without_a_timestamp_are_kept()
    {
        string[] left = await RunAsync(o => o.Keep<LogEntry>(TimeSpan.FromDays(30), e => e.ClosedAt));

        Assert.Equal(new[] { "audit:40:açık", "audit:5:kapalı", "audit:5:açık" }, left);
    }

    [Fact]
    public async Task Utc_datetime_columns_are_supported()
    {
        string[] left = await RunAsync(o => o.Keep<LogEntry>(TimeSpan.FromDays(30), e => e.CreatedUtc));

        Assert.Equal(2, left.Length);
    }

    [Fact]
    public async Task Deletion_runs_in_small_batches_until_done()
    {
        string[] left = await RunAsync(o => { o.Keep<LogEntry>(TimeSpan.FromDays(30), e => e.At); o.BatchSize = 1; });

        Assert.Equal(2, left.Length); // üç eski kayıt, birer birer
    }

    [Fact]
    public async Task One_run_stops_at_the_batch_limit_and_the_rest_waits_for_the_next_run()
    {
        string[] left = await RunAsync(o => { o.Keep<LogEntry>(TimeSpan.FromDays(30), e => e.At); o.BatchSize = 1; o.MaxBatchesPerRun = 2; });

        Assert.Equal(3, left.Length);
    }

    [Fact]
    public async Task Dry_run_deletes_nothing()
    {
        string[] left = await RunAsync(o => { o.Keep<LogEntry>(TimeSpan.FromDays(30), e => e.At); o.DryRun = true; });

        Assert.Equal(5, left.Length);
    }

    [Fact]
    public async Task Another_server_holding_the_lock_skips_this_run()
    {
        string[] left = await RunAsync(o => o.Keep<LogEntry>(TimeSpan.FromDays(30), e => e.At), s => s.AddSingleton<IDistributedLock>(new BusyLock()));

        Assert.Equal(5, left.Length);
    }

    [Fact]
    public void Nothing_is_deleted_unless_a_rule_is_given()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCanDataRetention<RetentionDbContext>(_ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddCanDataRetention<RetentionDbContext>(o => o.Keep<LogEntry>(TimeSpan.Zero, e => e.At)));
    }
}
