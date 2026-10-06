using System.Collections.Concurrent;
using Can.Core.MultiTenancy;
using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Can.Core.BackgroundJobs.Hangfire.Tests;

public sealed class Recorder
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries.ToArray();

    public void Add(string entry) => _entries.Enqueue(entry);

    public async Task WaitUntilAsync(Func<IReadOnlyCollection<string>, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition(Entries))
            await Task.Delay(50, timeout.Token);
    }
}

public sealed record GreetArgs(string Name);

public sealed class GreetJob(Recorder recorder, TenantContext tenant) : IBackgroundJob<GreetArgs>
{
    public Task ExecuteAsync(GreetArgs args, CancellationToken cancellationToken)
    {
        recorder.Add($"greet:{args.Name}@{tenant.TenantId ?? "host"}:{tenant.Tenant?.Identifier ?? "-"}");
        return Task.CompletedTask;
    }
}

public sealed class TickJob(Recorder recorder, TenantContext tenant) : IBackgroundJob
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        recorder.Add($"tick@{tenant.TenantId ?? "host"}");
        return Task.CompletedTask;
    }
}

// Hangfire yapılandırması statik (GlobalConfiguration/JobStorage.Current) olduğu için testler aynı sınıfta, sırayla çalışır.
public class HangfireTests
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
            ]
        );
        builder.Services.AddCanBackgroundJobs(typeof(HangfireTests).Assembly);
        builder.Services.AddCanHangfire(c => c.UseInMemoryStorage(), o => o.WorkerCount = 2);
        configure(builder.Services);

        IHost host = builder.Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Queue_is_replaced_by_hangfire_and_job_runs_with_captured_tenant()
    {
        using IHost host = await StartAsync(_ => { });

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set("t-a");
            IBackgroundJobQueue queue = scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>();
            Assert.IsType<HangfireBackgroundJobQueue>(queue);

            await queue.EnqueueAsync<GreetJob, GreetArgs>(new GreetArgs("ada"));
        }

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobQueue>().EnqueueAsync<TickJob>();

        Recorder recorder = host.Services.GetRequiredService<Recorder>();
        await recorder.WaitUntilAsync(e => e.Count >= 2);

        // Tenant id kuyruktan, TenantInfo ise çalıştırma anında ITenantStore'dan gelir.
        Assert.Contains("greet:ada@t-a:a", recorder.Entries);
        Assert.Contains("tick@host", recorder.Entries);

        await host.StopAsync();
    }

    [Fact]
    public async Task Recurring_job_is_registered_with_cron()
    {
        using IHost host = await StartAsync(s => s.AddCanHangfireRecurringJob<TickJob>("0 7 * * *", o => o.JobId = "gunluk-tick"));

        using IStorageConnection connection = host.Services.GetRequiredService<JobStorage>().GetConnection();
        RecurringJobDto job = Assert.Single(connection.GetRecurringJobs(), j => j.Id == "gunluk-tick");
        Assert.Equal("0 7 * * *", job.Cron);

        await host.StopAsync();
    }

    [Fact]
    public async Task Per_tenant_recurring_job_runs_once_for_each_active_tenant()
    {
        using IHost host = await StartAsync(s => s.AddCanHangfireRecurringJob<TickJob>(Cron.Daily(), o => o.PerTenant = true));

        host.Services.GetRequiredService<IRecurringJobManager>().Trigger(nameof(TickJob));

        Recorder recorder = host.Services.GetRequiredService<Recorder>();
        await recorder.WaitUntilAsync(e => e.Count >= 2);
        await Task.Delay(300);

        Assert.Equal(new[] { "tick@t-a", "tick@t-b" }, recorder.Entries.Order());

        await host.StopAsync();
    }
}
