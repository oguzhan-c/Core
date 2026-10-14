using Can.Core.Application;
using Can.Core.Persistence.RequestLogging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Tests;

public sealed class ActivityDbContext(DbContextOptions<ActivityDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddCanRequestActivityLog();
}

public class RequestActivityStoreTests
{
    [Fact]
    public async Task Batches_are_written_in_their_own_scope()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddDbContext<ActivityDbContext>(o => o.UseSqlite(connection));
        services.AddCanRequestActivityStore<ActivityDbContext>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ActivityDbContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        DateTimeOffset now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        RequestActivity Make(string user, int status) => new(now, "POST", "/api/orders", "/api/orders", null, status, 12.5, user, "t1", "s1", "10.0.0.1", "CanTest/1.0", "abc");

        IRequestActivitySink sink = provider.GetServices<IRequestActivitySink>().Single();
        await sink.WriteAsync([Make("u1", 201), Make("u1", 400), Make("u2", 201)], TestContext.Current.CancellationToken);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            ActivityDbContext db = scope.ServiceProvider.GetRequiredService<ActivityDbContext>();
            List<RequestActivityRecord> mine = await db.Set<RequestActivityRecord>().Where(a => a.UserId == "u1").ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, mine.Count);
            Assert.Contains(mine, a => a.StatusCode == 400);
            Assert.All(mine, a => Assert.Equal("t1", a.TenantId));
        }
    }
}
