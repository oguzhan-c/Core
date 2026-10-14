using System.Collections.Concurrent;
using Can.Core.Application;
using Can.Core.Persistence.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Tests;

public class EntityCacheInvalidationTests
{
    private sealed class RecordingTarget : ICacheInvalidationTarget
    {
        public ConcurrentQueue<string> Tags { get; } = new();

        public ValueTask InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
        {
            foreach (string tag in tags)
                Tags.Enqueue(tag);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<(TestHost Host, RecordingTarget Target)> CreateAsync(bool enabled = true)
    {
        var target = new RecordingTarget();
        TestHost host = await TestHost.CreateAsync(services =>
        {
            services.AddSingleton<ICacheInvalidationTarget>(target);
            if (enabled)
                services.AddCanEntityCacheInvalidation();
        });
        return (host, target);
    }

    [Fact]
    public void Entity_tag_ignores_generic_arity()
    {
        Assert.Equal("entity:Category", CacheTags.Entity<Category>());
        Assert.Equal("entity:List", CacheTags.Entity(typeof(List<int>)));
    }

    [Fact]
    public async Task Saved_entity_types_are_invalidated_once_per_save()
    {
        (TestHost host, RecordingTarget target) = await CreateAsync();
        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Categories.AddRange(new Category("Ofis"), new Category("Okul"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal(new[] { "entity:Category" }, target.Tags.ToArray());
        }
    }

    [Fact]
    public async Task Nothing_is_invalidated_when_not_enabled()
    {
        (TestHost host, RecordingTarget target) = await CreateAsync(enabled: false);
        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Categories.Add(new Category("Ofis"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Empty(target.Tags);
        }
    }

    [Fact]
    public async Task Inside_a_transaction_tags_wait_for_commit()
    {
        (TestHost host, RecordingTarget target) = await CreateAsync();
        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                db.Categories.Add(new Category("Ofis"));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                Assert.Empty(target.Tags); // commit'ten önce silinirse başka bir istek eski veriyi tekrar önbelleğe yazabilirdi

                await transaction.CommitAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(new[] { "entity:Category" }, target.Tags.ToArray());
        }
    }

    [Fact]
    public async Task Rolled_back_transaction_invalidates_nothing()
    {
        (TestHost host, RecordingTarget target) = await CreateAsync();
        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                db.Categories.Add(new Category("Ofis"));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            }

            db.ChangeTracker.Clear();
            await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
                await transaction.CommitAsync(TestContext.Current.CancellationToken); // boş commit: önceki etiketler kalmamalı

            Assert.Empty(target.Tags);
        }
    }
}
