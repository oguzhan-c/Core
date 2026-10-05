using System.Collections.Concurrent;
using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Can.Core.BackgroundJobs.Tests;

/// <summary>İşlerin çalıştığını ve hangi tenant adına çalıştığını kaydeder.</summary>
public sealed class Recorder
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries.ToArray();

    public void Add(string entry) => _entries.Enqueue(entry);

    public async Task WaitUntilAsync(Func<IReadOnlyCollection<string>, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition(Entries))
            await Task.Delay(20, timeout.Token);
    }
}

public sealed record GreetArgs(string Name);

public sealed class GreetJob(Recorder recorder, TenantContext tenant) : IBackgroundJob<GreetArgs>
{
    public Task ExecuteAsync(GreetArgs args, CancellationToken cancellationToken)
    {
        recorder.Add($"greet:{args.Name}@{tenant.TenantId ?? "host"}");
        return Task.CompletedTask;
    }
}

public sealed class FailingJob : IBackgroundJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("bilerek");
}

public sealed class TickJob(Recorder recorder, TenantContext tenant) : IBackgroundJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        recorder.Add($"tick@{tenant.TenantId ?? "host"}");
        return Task.CompletedTask;
    }
}

public class BackgroundJobTests
{
    private static async Task<IHost> StartAsync(Action<IServiceCollection> configure)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<Recorder>();
        builder.Services.AddCanMultiTenancy(o =>
            o.Tenants =
            [
                new TenantInfo { Id = "t-a", Identifier = "a" },
                new TenantInfo { Id = "t-b", Identifier = "b" },
                new TenantInfo { Id = "t-c", Identifier = "c", IsActive = false },
            ]);
        configure(builder.Services);

        IHost host = builder.Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Queued_job_runs_in_background_for_the_enqueuing_tenant()
    {
        using IHost host = await StartAsync(s => s.AddCanBackgroundJobs(typeof(GreetJob).Assembly));

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set("t-a");
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>().EnqueueAsync<GreetJob, GreetArgs>(new("ada"));
        }

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>().EnqueueAsync<GreetJob, GreetArgs>(new("linus"));
        }

        Recorder recorder = host.Services.GetRequiredService<Recorder>();
        await recorder.WaitUntilAsync(e => e.Count == 2);

        Assert.Equal(new[] { "greet:ada@t-a", "greet:linus@host" }, recorder.Entries);
        await host.StopAsync();
    }

    [Fact]
    public async Task Failing_job_does_not_stop_the_queue()
    {
        using IHost host = await StartAsync(s => s.AddCanBackgroundJobs(typeof(GreetJob).Assembly));

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            IBackgroundJobQueue queue = scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>();
            await queue.EnqueueAsync<FailingJob>();
            await queue.EnqueueAsync<GreetJob, GreetArgs>(new("sonra"));
        }

        Recorder recorder = host.Services.GetRequiredService<Recorder>();
        await recorder.WaitUntilAsync(e => e.Count == 1);

        Assert.Equal("greet:sonra@host", Assert.Single(recorder.Entries));
        await host.StopAsync();
    }

    [Fact]
    public async Task Recurring_job_runs_on_startup_and_then_periodically()
    {
        using IHost host = await StartAsync(s => s.AddCanRecurringJob<TickJob>(o => o.Interval = TimeSpan.FromMilliseconds(50)));

        Recorder recorder = host.Services.GetRequiredService<Recorder>();
        await recorder.WaitUntilAsync(e => e.Count >= 3);

        Assert.All(recorder.Entries, e => Assert.Equal("tick@host", e));
        await host.StopAsync();
    }

    [Fact]
    public async Task Per_tenant_recurring_job_runs_for_each_active_tenant()
    {
        using IHost host = await StartAsync(s => s.AddCanRecurringJob<TickJob>(o =>
        {
            o.Interval = TimeSpan.FromHours(1);
            o.PerTenant = true;
        }));

        Recorder recorder = host.Services.GetRequiredService<Recorder>();
        await recorder.WaitUntilAsync(e => e.Count >= 2);
        await Task.Delay(100);

        Assert.Equal(new[] { "tick@t-a", "tick@t-b" }, recorder.Entries.Order());
        await host.StopAsync();
    }
}
