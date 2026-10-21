using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CanFx.Application;
using CanFx.Persistence.DependencyInjection;
using CanFx.Persistence.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CanFx.Persistence.Tests;

public class QueryDiagnosticsTests
{
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        public string[] From<T>() => Messages.Where(m => m.StartsWith(typeof(T).FullName + "|", StringComparison.Ordinal)).Select(m => m[(m.IndexOf('|') + 1)..]).ToArray();

        private sealed class Logger(CapturedLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(category + "|" + formatter(state, exception));
        }
    }

    private static async Task<(TestHost Host, CapturedLogs Logs)> CreateAsync(Action<QueryDiagnosticsOptions> configure)
    {
        var logs = new CapturedLogs();
        QueryDiagnosticsOptions? options = null;
        TestHost host = await TestHost.CreateAsync(services =>
        {
            services.AddSingleton<ILoggerProvider>(logs);
            services.AddCanQueryDiagnostics(o => options = o); // kurulum (tablo oluşturma, seed) sınırsız çalışsın
        });
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Categories.AddRange(new Category("Ofis"), new Category("Okul"), new Category("Ev"), new Category("Bahçe"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        configure(options!); // ayarlar her komutta okunur: testin ayarları bundan sonra geçerli
        while (logs.Messages.TryDequeue(out _)) { } // kurulumun logları
        return (host, logs);
    }

    [Fact]
    public async Task Slow_queries_are_logged_with_their_source_and_tagged_sql()
    {
        (TestHost host, CapturedLogs logs) = await CreateAsync(o => o.SlowQueryThreshold = TimeSpan.FromTicks(1)); // her sorgu "yavaş"
        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            using (OperationContext.Begin("ListCategoriesQuery"))
                await db.Categories.ToListAsync(TestContext.Current.CancellationToken);

            string log = Assert.Single(logs.From<QueryDiagnosticsInterceptor>());
            Assert.Contains("kaynak: ListCategoriesQuery", log, StringComparison.Ordinal);
            Assert.Contains("-- can: ListCategoriesQuery\nSELECT", log, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Source_names_cannot_break_out_of_the_sql_comment()
    {
        (TestHost host, CapturedLogs logs) = await CreateAsync(o => o.SlowQueryThreshold = TimeSpan.FromTicks(1));
        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            using (OperationContext.Begin("Kötü\nisim; DROP TABLE"))
                await db.Categories.CountAsync(TestContext.Current.CancellationToken);

            Assert.Contains("-- can: K_t__isim_ DROP TABLE\nSELECT", Assert.Single(logs.From<QueryDiagnosticsInterceptor>()), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Fast_queries_are_not_logged_and_nothing_is_tagged_without_a_source()
    {
        (TestHost host, CapturedLogs logs) = await CreateAsync(o => o.SlowQueryThreshold = TimeSpan.FromMinutes(1));
        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Categories.ToListAsync(TestContext.Current.CancellationToken);
            Assert.Empty(logs.From<QueryDiagnosticsInterceptor>());
        }
    }

    [Fact]
    public async Task Repeated_query_in_one_scope_is_reported_once_as_n_plus_one()
    {
        (TestHost host, CapturedLogs logs) = await CreateAsync(o => { o.SlowQueryThreshold = TimeSpan.Zero; o.RepeatedQueryThreshold = 3; });
        await using (host)
        {
            await using (AsyncServiceScope scope = host.CreateScope())
            {
                TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
                using (OperationContext.Begin("ListOrdersQuery"))
                {
                    foreach (int id in new[] { 1, 2, 3, 4, 1 })
                        await db.Categories.Where(c => c.Id == id).FirstOrDefaultAsync(TestContext.Current.CancellationToken); // döngüde sorgu
                }

                string log = Assert.Single(logs.From<QueryDiagnosticsInterceptor>());
                Assert.StartsWith("Olası N+1: aynı sorgu bir istekte 3 kez çalıştı (kaynak: ListOrdersQuery)", log, StringComparison.Ordinal);
            }

            // Yeni istek (scope): sayaç sıfırdan
            await using (AsyncServiceScope scope = host.CreateScope())
            {
                TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
                foreach (int id in new[] { 1, 2 })
                    await db.Categories.Where(c => c.Id == id).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
            }

            Assert.Single(logs.From<QueryDiagnosticsInterceptor>());
        }
    }

    [Fact]
    public async Task Violations_can_fail_fast_in_tests()
    {
        (TestHost host, _) = await CreateAsync(o => { o.SlowQueryThreshold = TimeSpan.Zero; o.MaxCommandsPerScope = 2; o.ThrowOnViolation = true; });
        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Categories.CountAsync(TestContext.Current.CancellationToken);
            await db.Categories.AnyAsync(TestContext.Current.CancellationToken);

            QueryDiagnosticsException error = await Assert.ThrowsAsync<QueryDiagnosticsException>(() => db.Categories.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Contains("2 komut sınırı", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Command_durations_are_published_as_metrics()
    {
        (TestHost host, _) = await CreateAsync(o => o.SlowQueryThreshold = TimeSpan.Zero);
        var sources = new ConcurrentBag<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "can.db.command.duration")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "can.source")
                    sources.Add(tag.Value as string);
            }
        });
        listener.Start();

        await using (host)
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            using (OperationContext.Begin("MetricProbeQuery"))
                await db.Categories.CountAsync(TestContext.Current.CancellationToken);
        }

        Assert.Contains("MetricProbeQuery", sources);
    }

    [Fact]
    public void Operation_context_restores_the_outer_name()
    {
        Assert.Null(OperationContext.Current);
        using (OperationContext.Begin("Outer"))
        {
            using (OperationContext.Begin("Inner"))
                Assert.Equal("Inner", OperationContext.Current);
            Assert.Equal("Outer", OperationContext.Current);
        }

        Assert.Null(OperationContext.Current);
    }
}
