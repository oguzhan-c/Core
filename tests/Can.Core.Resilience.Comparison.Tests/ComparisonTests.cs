using System.Diagnostics;
using Xunit.Abstractions;
using Polly; // Polly'nin AddRetry, AddTimeout ... uzantı metotları (adlar bu namespace'te önce bizim tiplere çözülür)
using P = Polly;

namespace Can.Core.Resilience.Comparison.Tests;

/// <summary>
/// Aynı senaryo hem Can.Core.Resilience hem Polly ile çalıştırılır ve gözlenen davranış (çağrı sayısı, bekleme
/// süreleri, sonuç, exception tipi, devre durumları) karşılaştırılır.
/// </summary>
public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override long GetTimestamp() => Now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}

public class RetryComparisonTests
{
    public static TheoryData<DelayBackoffType, P.DelayBackoffType> BackoffTypes => new()
    {
        { DelayBackoffType.Constant, P.DelayBackoffType.Constant },
        { DelayBackoffType.Linear, P.DelayBackoffType.Linear },
        { DelayBackoffType.Exponential, P.DelayBackoffType.Exponential },
    };

    [Theory]
    [MemberData(nameof(BackoffTypes))]
    public async Task Same_attempts_delays_and_result(DelayBackoffType ours, P.DelayBackoffType polly)
    {
        // Gerçek bekleme olmasın diye çok kısa temel süre; karşılaştırılan hesaplanan bekleme listesi.
        TimeSpan delay = TimeSpan.FromMilliseconds(1);
        TimeSpan maxDelay = TimeSpan.FromMilliseconds(3);

        var oursDelays = new List<TimeSpan>();
        int oursCalls = 0;
        int oursResult = await new ResiliencePipelineBuilder()
            .AddRetry(new RetryOptions
            {
                MaxRetryAttempts = 4,
                Delay = delay,
                MaxDelay = maxDelay,
                BackoffType = ours,
                OnRetry = a =>
                {
                    oursDelays.Add(a.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            })
            .Build()
            .ExecuteAsync(_ => ++oursCalls < 5 ? throw new InvalidOperationException() : ValueTask.FromResult(oursCalls));

        var pollyDelays = new List<TimeSpan>();
        int pollyCalls = 0;
        int pollyResult = await new P.ResiliencePipelineBuilder()
            .AddRetry(new P.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 4,
                Delay = delay,
                MaxDelay = maxDelay,
                BackoffType = polly,
                UseJitter = false,
                ShouldHandle = new P.PredicateBuilder().Handle<InvalidOperationException>(),
                OnRetry = a =>
                {
                    pollyDelays.Add(a.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            })
            .Build()
            .ExecuteAsync(_ => ++pollyCalls < 5 ? throw new InvalidOperationException() : ValueTask.FromResult(pollyCalls));

        Assert.Equal(pollyCalls, oursCalls);
        Assert.Equal(pollyResult, oursResult);
        Assert.Equal(pollyDelays, oursDelays);
    }

    [Fact]
    public async Task Same_behavior_when_retries_are_exhausted_or_exception_is_not_handled()
    {
        async Task<(int Calls, Type Exception)> OursAsync(Exception toThrow)
        {
            int calls = 0;
            ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
                .AddRetry(new RetryOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.Zero,
                    ShouldHandle = new PredicateBuilder<object?>().Handle<TimeoutException>(),
                })
                .Build();

            Exception e = await Assert.ThrowsAnyAsync<Exception>(async () => await pipeline.ExecuteAsync<int>(_ =>
            {
                calls++;
                throw toThrow;
            }));
            return (calls, e.GetType());
        }

        async Task<(int Calls, Type Exception)> PollyAsync(Exception toThrow)
        {
            int calls = 0;
            P.ResiliencePipeline pipeline = new P.ResiliencePipelineBuilder()
                .AddRetry(new P.Retry.RetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.Zero,
                    ShouldHandle = new P.PredicateBuilder().Handle<TimeoutException>(),
                })
                .Build();

            Exception e = await Assert.ThrowsAnyAsync<Exception>(async () => await pipeline.ExecuteAsync<int>(_ =>
            {
                calls++;
                throw toThrow;
            }));
            return (calls, e.GetType());
        }

        Assert.Equal(await PollyAsync(new TimeoutException()), await OursAsync(new TimeoutException()));     // 3 çağrı
        Assert.Equal(await PollyAsync(new ArgumentException()), await OursAsync(new ArgumentException()));   // 1 çağrı
    }

    [Fact]
    public async Task Same_result_based_retry()
    {
        int oursCalls = 0;
        int ours = await new ResiliencePipelineBuilder<int>()
            .AddRetry(new RetryOptions<int> { Delay = TimeSpan.Zero, ShouldHandle = o => o.Result < 0 })
            .Build()
            .ExecuteAsync(_ => ValueTask.FromResult(++oursCalls < 3 ? -1 : 10));

        int pollyCalls = 0;
        int polly = await new P.ResiliencePipelineBuilder<int>()
            .AddRetry(new P.Retry.RetryStrategyOptions<int> { Delay = TimeSpan.Zero, ShouldHandle = a => ValueTask.FromResult(a.Outcome.Result < 0) })
            .Build()
            .ExecuteAsync(_ => ValueTask.FromResult(++pollyCalls < 3 ? -1 : 10));

        Assert.Equal(polly, ours);
        Assert.Equal(pollyCalls, oursCalls);
    }
}

public class CircuitBreakerComparisonTests
{
    // Senaryo: 2 başarı, 2 hata (oran %50, eşik 4 istek) → açık → reddedilen çağrı → süre dolar → yoklama
    private static readonly bool[] Script = [true, true, false, false];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Same_state_transitions(bool probeSucceeds)
    {
        Assert.Equal(await PollyTraceAsync(probeSucceeds), await OursTraceAsync(probeSucceeds));
    }

    private static async Task<List<string>> OursTraceAsync(bool probeSucceeds)
    {
        var clock = new ManualClock();
        var state = new CircuitBreakerStateProvider();
        var trace = new List<string>();
        int calls = 0;

        ResiliencePipeline pipeline = new ResiliencePipelineBuilder { TimeProvider = clock }
            .AddCircuitBreaker(new CircuitBreakerOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                ShouldHandle = new PredicateBuilder<object?>().Handle<InvalidOperationException>(),
                StateProvider = state,
            })
            .Build();

        async Task RunAsync(bool success)
        {
            try
            {
                await pipeline.ExecuteAsync(_ =>
                {
                    calls++;
                    return success ? ValueTask.FromResult(1) : throw new InvalidOperationException();
                });
                trace.Add($"ok/{state.CircuitState}/{calls}");
            }
            catch (BrokenCircuitException)
            {
                trace.Add($"rejected/{state.CircuitState}/{calls}");
            }
            catch (InvalidOperationException)
            {
                trace.Add($"failed/{state.CircuitState}/{calls}");
            }
        }

        foreach (bool success in Script)
            await RunAsync(success);
        await RunAsync(true);
        clock.Now += TimeSpan.FromSeconds(6);
        await RunAsync(probeSucceeds);
        await RunAsync(true);
        return trace;
    }

    private static async Task<List<string>> PollyTraceAsync(bool probeSucceeds)
    {
        var clock = new ManualClock();
        var state = new P.CircuitBreaker.CircuitBreakerStateProvider();
        var trace = new List<string>();
        int calls = 0;

        P.ResiliencePipeline pipeline = new P.ResiliencePipelineBuilder { TimeProvider = clock }
            .AddCircuitBreaker(new P.CircuitBreaker.CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                ShouldHandle = new P.PredicateBuilder().Handle<InvalidOperationException>(),
                StateProvider = state,
            })
            .Build();

        async Task RunAsync(bool success)
        {
            try
            {
                await pipeline.ExecuteAsync(_ =>
                {
                    calls++;
                    return success ? ValueTask.FromResult(1) : throw new InvalidOperationException();
                });
                trace.Add($"ok/{Map(state.CircuitState)}/{calls}");
            }
            catch (P.CircuitBreaker.BrokenCircuitException)
            {
                trace.Add($"rejected/{Map(state.CircuitState)}/{calls}");
            }
            catch (InvalidOperationException)
            {
                trace.Add($"failed/{Map(state.CircuitState)}/{calls}");
            }
        }

        foreach (bool success in Script)
            await RunAsync(success);
        await RunAsync(true);
        clock.Now += TimeSpan.FromSeconds(6);
        await RunAsync(probeSucceeds);
        await RunAsync(true);
        return trace;
    }

    private static CircuitState Map(P.CircuitBreaker.CircuitState state) => Enum.Parse<CircuitState>(state.ToString());
}

public class OtherStrategyComparisonTests
{
    [Fact]
    public async Task Timeout_both_reject_slow_work_with_timeout_exception()
    {
        static async ValueTask<int> SlowAsync(CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return 1;
        }

        Exception ours = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromMilliseconds(50)).Build().ExecuteAsync(SlowAsync));
        Exception polly = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await new P.ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromMilliseconds(50)).Build().ExecuteAsync(SlowAsync));

        Assert.IsType<TimeoutRejectedException>(ours);
        Assert.IsType<P.Timeout.TimeoutRejectedException>(polly);
        Assert.Equal(polly.GetType().Name, ours.GetType().Name);
    }

    [Fact]
    public async Task Fallback_both_return_substitute()
    {
        string ours = await new ResiliencePipelineBuilder<string>()
            .AddFallback(new FallbackOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<HttpRequestException>(),
                FallbackAction = _ => ValueTask.FromResult(Outcome.FromResult("yedek")),
            })
            .Build()
            .ExecuteAsync(_ => throw new HttpRequestException());

        string polly = await new P.ResiliencePipelineBuilder<string>()
            .AddFallback(new P.Fallback.FallbackStrategyOptions<string>
            {
                ShouldHandle = new P.PredicateBuilder<string>().Handle<HttpRequestException>(),
                FallbackAction = _ => P.Outcome.FromResultAsValueTask("yedek"),
            })
            .Build()
            .ExecuteAsync<string>(_ => throw new HttpRequestException());

        Assert.Equal(polly, ours);
    }

    [Fact]
    public async Task Hedging_both_pick_the_fast_attempt()
    {
        static Func<CancellationToken, ValueTask<string>> Work()
        {
            int attempts = 0;
            return async ct =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return "yavaş";
                }

                return "hızlı";
            };
        }

        Func<CancellationToken, ValueTask<string>> oursWork = Work();
        string ours = await new ResiliencePipelineBuilder<string>()
            .AddHedging(new HedgingOptions<string> { MaxHedgedAttempts = 1, Delay = TimeSpan.FromMilliseconds(50) })
            .Build()
            .ExecuteAsync(c => oursWork(c.CancellationToken), new ResilienceContext());

        Func<CancellationToken, ValueTask<string>> pollyWork = Work();
        string polly = await new P.ResiliencePipelineBuilder<string>()
            .AddHedging(new P.Hedging.HedgingStrategyOptions<string> { MaxHedgedAttempts = 1, Delay = TimeSpan.FromMilliseconds(50) })
            .Build()
            .ExecuteAsync(ct => pollyWork(ct));

        Assert.Equal(polly, ours);
        Assert.Equal("hızlı", ours);
    }

    [Fact]
    public async Task Concurrency_limiter_both_reject_when_full()
    {
        var gate = new TaskCompletionSource();

        async Task<string> RunAsync(Func<Func<CancellationToken, ValueTask<int>>, ValueTask<int>> execute)
        {
            ValueTask<int> first = execute(async _ =>
            {
                await gate.Task;
                return 1;
            });

            try
            {
                await execute(_ => ValueTask.FromResult(2));
                return "kabul";
            }
            catch (Exception e)
            {
                return e.GetType().Name;
            }
            finally
            {
                gate.TrySetResult();
                await first;
            }
        }

        ResiliencePipeline ours = new ResiliencePipelineBuilder().AddConcurrencyLimiter(1).Build();
        string oursResult = await RunAsync(w => ours.ExecuteAsync(w));

        gate = new TaskCompletionSource();
        P.ResiliencePipeline polly = new P.ResiliencePipelineBuilder().AddConcurrencyLimiter(1, 0).Build();
        string pollyResult = await RunAsync(w => polly.ExecuteAsync(w));

        Assert.Equal(pollyResult, oursResult);
        Assert.Equal("RateLimiterRejectedException", oursResult);
    }
}

/// <summary>Kaba bir ek yük ölçümü (sonuç test çıktısına yazılır; doğrulama gevşek tutulur).</summary>
public class OverheadComparisonTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Overhead_of_retry_plus_timeout_on_success_path()
    {
        const int iterations = 50_000;

        ResiliencePipeline ours = new ResiliencePipelineBuilder()
            .AddRetry(new RetryOptions { Delay = TimeSpan.Zero })
            .AddTimeout(TimeSpan.FromSeconds(5))
            .Build();

        P.ResiliencePipeline polly = new P.ResiliencePipelineBuilder()
            .AddRetry(new P.Retry.RetryStrategyOptions { Delay = TimeSpan.Zero })
            .AddTimeout(TimeSpan.FromSeconds(5))
            .Build();

        static ValueTask<int> Work(CancellationToken _) => ValueTask.FromResult(1);

        // ısınma
        for (int i = 0; i < 1_000; i++)
        {
            await ours.ExecuteAsync(Work);
            await polly.ExecuteAsync(Work);
        }

        long oursAlloc = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            await ours.ExecuteAsync(Work);
        TimeSpan oursTime = watch.Elapsed;
        oursAlloc = GC.GetAllocatedBytesForCurrentThread() - oursAlloc;

        long pollyAlloc = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        for (int i = 0; i < iterations; i++)
            await polly.ExecuteAsync(Work);
        TimeSpan pollyTime = watch.Elapsed;
        pollyAlloc = GC.GetAllocatedBytesForCurrentThread() - pollyAlloc;

        output.WriteLine($"Can.Core.Resilience: {oursTime.TotalMilliseconds * 1000 / iterations:0.00} µs/çağrı, {oursAlloc / iterations} B/çağrı");
        output.WriteLine($"Polly               : {pollyTime.TotalMilliseconds * 1000 / iterations:0.00} µs/çağrı, {pollyAlloc / iterations} B/çağrı");

        Assert.True(oursTime > TimeSpan.Zero && pollyTime > TimeSpan.Zero);
    }
}

/// <summary>Ek yükün hangi stratejiden geldiğini görmek için ayrı ayrı ölçüm.</summary>
public class OverheadBreakdownTests(ITestOutputHelper output)
{
    private const int Iterations = 20_000;

    private static ValueTask<int> Work(CancellationToken _) => ValueTask.FromResult(1);

    private static async Task<(double Micro, long Bytes)> MeasureAsync(Func<ValueTask<int>> run)
    {
        for (int i = 0; i < 500; i++)
            await run();

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
            await run();
        watch.Stop();

        return (watch.Elapsed.TotalMilliseconds * 1000 / Iterations, (GC.GetAllocatedBytesForCurrentThread() - allocated) / Iterations);
    }

    [Fact]
    public async Task Breakdown_by_strategy()
    {
        var cases = new (string Name, ResiliencePipeline Ours, P.ResiliencePipeline Polly)[]
        {
            ("boş", new ResiliencePipelineBuilder().Build(), new P.ResiliencePipelineBuilder().Build()),
            ("retry", new ResiliencePipelineBuilder().AddRetry(new RetryOptions { Delay = TimeSpan.Zero }).Build(),
                new P.ResiliencePipelineBuilder().AddRetry(new P.Retry.RetryStrategyOptions { Delay = TimeSpan.Zero }).Build()),
            ("timeout", new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(5)).Build(),
                new P.ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(5)).Build()),
            ("circuit breaker", new ResiliencePipelineBuilder().AddCircuitBreaker(new CircuitBreakerOptions()).Build(),
                new P.ResiliencePipelineBuilder().AddCircuitBreaker(new P.CircuitBreaker.CircuitBreakerStrategyOptions()).Build()),
            ("concurrency", new ResiliencePipelineBuilder().AddConcurrencyLimiter(100).Build(),
                new P.ResiliencePipelineBuilder().AddConcurrencyLimiter(100).Build()),
        };

        foreach ((string name, ResiliencePipeline ours, P.ResiliencePipeline polly) in cases)
        {
            (double oursMicro, long oursBytes) = await MeasureAsync(() => ours.ExecuteAsync(Work));
            (double pollyMicro, long pollyBytes) = await MeasureAsync(() => polly.ExecuteAsync(Work));
            output.WriteLine($"{name,-16} biz: {oursMicro,6:0.00} µs {oursBytes,6} B | Polly: {pollyMicro,6:0.00} µs {pollyBytes,6} B");
        }
    }
}
