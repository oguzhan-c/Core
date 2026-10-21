using CanFx.Persistence.HealthChecks;
using CanFx.Persistence.Outbox;
using CanFx.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CanFx.Persistence.Tests;

public class OutboxHealthCheckTests
{
    private static async Task DiscontinueAsync(TestHost host)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
        Product product = await repository.AddAsync(Product.Create("Defter", 5));
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        product.Discontinue();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private static Task<HealthCheckResult> CheckAsync(TestHost host, OutboxHealthCheckOptions? options = null)
    {
        var check = new OutboxHealthCheck<TestDbContext>(host.Services.GetRequiredService<IServiceScopeFactory>(), options ?? new OutboxHealthCheckOptions(), host.Clock);
        var registration = new HealthCheckRegistration("outbox", check, HealthStatus.Degraded, null);
        return check.CheckHealthAsync(new HealthCheckContext { Registration = registration });
    }

    private static OutboxProcessor<TestDbContext> Processor(TestHost host) =>
        ActivatorUtilities.CreateInstance<OutboxProcessor<TestDbContext>>(host.Services, new OutboxOptions());

    [Fact]
    public async Task Healthy_when_nothing_is_stuck()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await DiscontinueAsync(host);

        HealthCheckResult result = await CheckAsync(host);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, result.Data["pending"]);
        Assert.Equal(0, result.Data["dead"]);
    }

    [Fact]
    public async Task Degraded_when_oldest_pending_message_is_too_old()
    {
        await using TestHost host = await TestHost.CreateAsync();
        host.Clock.Now = DateTimeOffset.UtcNow; // event zamanı (DomainEvent.OccurredAt) gerçek saatten gelir
        await DiscontinueAsync(host);
        host.Clock.Now += TimeSpan.FromMinutes(20);

        HealthCheckResult result = await CheckAsync(host);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("geride", result.Description, StringComparison.Ordinal);
        Assert.InRange((double)result.Data["oldestPendingSeconds"], 1190, 1210);
    }

    [Fact]
    public async Task Degraded_when_dead_messages_exist()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await DiscontinueAsync(host);
        host.Services.GetRequiredService<FailureSwitch>().Permanent = true;
        await Processor(host).ProcessAsync();

        HealthCheckResult result = await CheckAsync(host);
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(1, result.Data["dead"]);

        HealthCheckResult relaxed = await CheckAsync(host, new OutboxHealthCheckOptions { DegradeOnDeadMessages = false });
        Assert.Equal(HealthStatus.Healthy, relaxed.Status);
    }
}
