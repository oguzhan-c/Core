using System.Text.Json;
using CanFx.Persistence.AuditTrail;
using CanFx.Persistence.DependencyInjection;
using CanFx.Persistence.Outbox;
using CanFx.Persistence.Repositories;
using CanFx.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Persistence.Tests;

public sealed class CategorySeeder(TestDbContext db, EventLog log) : IDataSeeder
{
    public int Order => 1;

    public async Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken)
    {
        log.Add("seed:category");
        if (await db.Categories.AnyAsync(cancellationToken))
            return;

        db.Categories.Add(new Category("Kırtasiye"));
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class FirstSeeder(EventLog log) : IDataSeeder
{
    public int Order => -1;

    public Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken)
    {
        log.Add($"seed:first:{(context.IsHost ? "host" : context.Tenant!.Id)}");
        return Task.CompletedTask;
    }
}

public class OutboxAuditSeedingTests
{
    // ---------------------------------------------------------------- outbox

    private static async Task<int> AddAndDiscontinueAsync(TestHost host)
    {
        int id;
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            Product product = await repository.AddAsync(Product.Create("Defter", 5));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            id = product.Id;
        }

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            Product product = (await repository.GetByIdAsync(id))!;
            product.Discontinue();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        return id;
    }

    private static async Task<OutboxMessage[]> OutboxAsync(TestHost host)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestDbContext>().OutboxMessages.AsNoTracking().ToArrayAsync();
    }

    private static OutboxProcessor<TestDbContext> Processor(TestHost host, OutboxOptions? options = null) =>
        ActivatorUtilities.CreateInstance<OutboxProcessor<TestDbContext>>(host.Services, options ?? new OutboxOptions());

    [Fact]
    public async Task Integration_event_is_stored_in_outbox_instead_of_published_immediately()
    {
        await using TestHost host = await TestHost.CreateAsync();

        int id = await AddAndDiscontinueAsync(host);

        Assert.DoesNotContain(host.Events.Entries, e => e.StartsWith("discontinued", StringComparison.Ordinal));
        Assert.Contains("created:Defter", host.Events.Entries); // normal event'ler hâlâ hemen yayınlanır

        OutboxMessage message = Assert.Single(await OutboxAsync(host));
        Assert.Null(message.ProcessedAt);
        Assert.Equal(FakeCurrentTenant.TenantA.ToString(), message.TenantId);
        Assert.Contains(nameof(ProductDiscontinued), message.Type, StringComparison.Ordinal);
        var restored = Assert.IsType<ProductDiscontinued>(message.Deserialize());
        Assert.Equal(id, restored.ProductId);
        Assert.Equal("Defter", restored.Name);
        Assert.Equal(message.EventId, restored.EventId);
    }

    [Fact]
    public async Task Processor_publishes_pending_messages_once()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await AddAndDiscontinueAsync(host);

        Assert.Equal(1, await Processor(host).ProcessAsync());
        Assert.Equal(0, await Processor(host).ProcessAsync());

        Assert.Single(host.Events.Entries, e => e == "discontinued:Defter");
        Assert.NotNull(Assert.Single(await OutboxAsync(host)).ProcessedAt);
    }

    [Fact]
    public async Task Failed_message_is_retried_on_next_run()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await AddAndDiscontinueAsync(host);
        FailureSwitch failure = host.Services.GetRequiredService<FailureSwitch>();

        failure.Fail = true;
        Assert.Equal(0, await Processor(host).ProcessAsync());

        OutboxMessage failed = Assert.Single(await OutboxAsync(host));
        Assert.Equal(1, failed.Attempts);
        Assert.Contains("handler hatası", failed.LastError, StringComparison.Ordinal);
        Assert.Null(failed.ProcessedAt);
        Assert.Equal(OutboxMessageStatus.Pending, failed.Status);
        Assert.InRange(failed.NextAttemptAt!.Value - host.Clock.Now.UtcDateTime, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)); // 5 sn ± %20

        failure.Fail = false;
        Assert.Equal(0, await Processor(host).ProcessAsync()); // bekleme süresi dolmadı

        host.Clock.Now += TimeSpan.FromSeconds(10);
        Assert.Equal(1, await Processor(host).ProcessAsync());
        Assert.Contains("discontinued:Defter", host.Events.Entries);

        OutboxMessage published = Assert.Single(await OutboxAsync(host));
        Assert.Equal(OutboxMessageStatus.Processed, published.Status);
        Assert.Null(published.NextAttemptAt);
        Assert.Null(published.LastError);
    }

    [Fact]
    public async Task Message_dies_after_max_attempts_and_can_be_requeued()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await AddAndDiscontinueAsync(host);
        FailureSwitch failure = host.Services.GetRequiredService<FailureSwitch>();
        var options = new OutboxOptions { MaxAttempts = 3, RetryJitter = 0 };

        failure.Fail = true;
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(0, await Processor(host, options).ProcessAsync());
            host.Clock.Now += TimeSpan.FromMinutes(1);
        }

        OutboxMessage dead = Assert.Single(await OutboxAsync(host));
        Assert.Equal(OutboxMessageStatus.Dead, dead.Status);
        Assert.Equal(3, dead.Attempts);
        Assert.NotNull(dead.DeadAt);
        Assert.Null(dead.NextAttemptAt);

        // ölü mesaj kendiliğinden denenmez
        failure.Fail = false;
        host.Clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(0, await Processor(host, options).ProcessAsync());

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            Assert.Equal(1, await db.OutboxMessages.Where(m => m.Id == dead.Id).RequeueAsync());
        }

        OutboxMessage requeued = Assert.Single(await OutboxAsync(host));
        Assert.Equal(OutboxMessageStatus.Pending, requeued.Status);
        Assert.Equal(0, requeued.Attempts);
        Assert.Null(requeued.DeadAt);

        Assert.Equal(1, await Processor(host, options).ProcessAsync());
        Assert.Contains("discontinued:Defter", host.Events.Entries);

        // yayınlanmış mesaj yeniden kuyruğa alınamaz
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            Assert.Equal(0, await db.OutboxMessages.RequeueAsync());
        }
    }

    [Fact]
    public async Task Permanent_failure_dies_without_retries()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await AddAndDiscontinueAsync(host);
        host.Services.GetRequiredService<FailureSwitch>().Permanent = true;

        Assert.Equal(0, await Processor(host).ProcessAsync());

        OutboxMessage dead = Assert.Single(await OutboxAsync(host));
        Assert.Equal(OutboxMessageStatus.Dead, dead.Status);
        Assert.Equal(1, dead.Attempts);
        Assert.Contains("ürün artık yok", dead.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dead_messages_are_kept_unless_retention_is_set()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await AddAndDiscontinueAsync(host);
        host.Services.GetRequiredService<FailureSwitch>().Permanent = true;
        await Processor(host).ProcessAsync();

        host.Clock.Now += TimeSpan.FromDays(30);
        await Processor(host).ProcessAsync();
        Assert.Single(await OutboxAsync(host));

        await Processor(host, new OutboxOptions { RetainDeadMessagesFor = TimeSpan.FromDays(7) }).ProcessAsync();
        Assert.Empty(await OutboxAsync(host));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(4, 40)]
    [InlineData(10, 2560)]
    [InlineData(11, 3600)] // üst sınır
    [InlineData(40, 3600)]
    public void Retry_delay_doubles_up_to_the_cap(int attempts, int seconds)
    {
        var options = new OutboxOptions { RetryJitter = 0 };
        Assert.Equal(TimeSpan.FromSeconds(seconds), options.RetryDelay(attempts, random: 0.5));
    }

    [Fact]
    public void Retry_delay_jitter_stays_within_bounds()
    {
        var options = new OutboxOptions { RetryJitter = 0.2 };
        Assert.Equal(8, options.RetryDelay(2, random: 0).TotalSeconds, precision: 3);   // 10 × 0,8
        Assert.Equal(12, options.RetryDelay(2, random: 1).TotalSeconds, precision: 3);  // 10 × 1,2
    }

    [Fact]
    public async Task Failed_save_does_not_leave_duplicate_outbox_messages()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await AddAndDiscontinueAsync(host);

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            Product product = await db.Products.FirstAsync();
            product.Discontinue();

            // Name NOT NULL: kayıt veritabanında başarısız olur, event aggregate'te kalır.
            var invalid = new Customer(null!, "x");
            db.Customers.Add(invalid);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            // Düzeltip tekrar kaydedince event outbox'a bir kez yazılır.
            invalid.Name = "Grace";
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, (await OutboxAsync(host)).Length);
    }

    // ---------------------------------------------------------------- audit trail

    private static async Task<AuditLog[]> AuditLogsAsync(TestHost host)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<TestDbContext>().AuditLogs.AsNoTracking().ToListAsync())
            .OrderBy(l => l.Id)
            .ToArray();
    }

    private static JsonElement Changes(AuditLog log) => JsonDocument.Parse(log.Changes!).RootElement;

    [Fact]
    public async Task Audited_entity_changes_are_recorded_with_user_tenant_and_values()
    {
        await using TestHost host = await TestHost.CreateAsync();

        int id;
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            var customer = new Customer("Ada", "gizli-1");
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            id = customer.Id;
        }

        host.User.Id = "user-2";
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            Customer customer = await db.Customers.SingleAsync();
            customer.Name = "Ada L.";
            customer.Secret = "gizli-2";
            await db.SaveChangesAsync();

            db.Customers.Remove(customer); // soft delete
            await db.SaveChangesAsync();
        }

        AuditLog[] logs = await AuditLogsAsync(host);
        Assert.Equal(new[] { AuditAction.Created, AuditAction.Updated, AuditAction.Deleted }, logs.Select(l => l.Action));
        Assert.All(logs, l => Assert.Equal(nameof(Customer), l.EntityType));
        Assert.All(logs, l => Assert.Equal(id.ToString(System.Globalization.CultureInfo.InvariantCulture), l.EntityId)); // identity anahtarı kayıttan sonra yazılır
        Assert.All(logs, l => Assert.Equal(FakeCurrentTenant.TenantA.ToString(), l.TenantId));
        Assert.Equal(new[] { "user-1", "user-2", "user-2" }, logs.Select(l => l.UserId));

        JsonElement created = Changes(logs[0]);
        Assert.Equal("Ada", created.GetProperty("Name").GetProperty("new").GetString());
        Assert.False(created.TryGetProperty("Secret", out _)); // [DisableAuditing]
        Assert.False(created.TryGetProperty("CreatedAt", out _)); // audit alanları yazılmaz

        JsonElement updated = Changes(logs[1]);
        Assert.Equal("Ada", updated.GetProperty("Name").GetProperty("old").GetString());
        Assert.Equal("Ada L.", updated.GetProperty("Name").GetProperty("new").GetString());
        Assert.False(updated.TryGetProperty("Secret", out _));

        Assert.True(Changes(logs[2]).GetProperty("IsDeleted").GetProperty("new").GetBoolean());
    }

    [Fact]
    public async Task Entities_without_audited_attribute_are_not_recorded_by_default()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await AddAndDiscontinueAsync(host);

        Assert.Empty(await AuditLogsAsync(host));
    }

    [Fact]
    public async Task All_entities_can_be_audited()
    {
        await using TestHost host = await TestHost.CreateAsync(s => s.AddCanAuditTrail(o => o.AuditAllEntities = true));
        await AddAndDiscontinueAsync(host);

        AuditLog log = Assert.Single(await AuditLogsAsync(host));
        Assert.Equal(nameof(Product), log.EntityType);
        Assert.Equal(AuditAction.Created, log.Action);
    }

    // ---------------------------------------------------------------- seed

    [Fact]
    public async Task Seeders_run_in_order_and_are_idempotent()
    {
        await using TestHost host = await TestHost.CreateAsync(s => s.AddCanDataSeeders(typeof(CategorySeeder).Assembly));

        await host.Services.SeedDataAsync();
        await host.Services.SeedDataAsync();

        Assert.Equal(new[] { "seed:first:host", "seed:category", "seed:first:host", "seed:category" }, host.Events.Entries);

        await using AsyncServiceScope scope = host.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<TestDbContext>().Categories.CountAsync());
    }
}
