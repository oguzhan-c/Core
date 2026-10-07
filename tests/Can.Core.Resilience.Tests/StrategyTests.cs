using System.Threading.RateLimiting;

namespace Can.Core.Resilience.Tests;

/// <summary>Elle ilerletilen saat (yalnızca "şimdi"; beklemeler gerçek zamanlıdır).</summary>
public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

public class RetryTests
{
    [Fact]
    public async Task Retries_handled_failures_until_success()
    {
        int calls = 0;
        var retries = new List<int>();
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(1),
                OnRetry = args =>
                {
                    retries.Add(args.AttemptNumber);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

        int result = await pipeline.ExecuteAsync(_ => ++calls < 3 ? throw new InvalidOperationException("geçici") : ValueTask.FromResult(42));

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { 0, 1 }, retries);
    }

    [Fact]
    public async Task Retries_are_reported_as_metrics()
    {
        int retries = 0;
        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ResilienceTelemetry.Name)
                l.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (tags.ToArray().Any(t => t.Key == "can.resilience.operation" && (string?)t.Value == "metrik-testi"))
                Interlocked.Add(ref retries, (int)value);
        });
        meterListener.Start();

        int calls = 0;
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder().AddRetry(new RetryOptions { MaxRetryAttempts = 3, Delay = TimeSpan.Zero }).Build();
        await pipeline.ExecuteAsync(
            _ => ++calls < 3 ? throw new InvalidOperationException() : ValueTask.FromResult(1),
            new ResilienceContext(operationKey: "metrik-testi")
        );

        Assert.Equal(2, retries);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_rethrows_original()
    {
        int calls = 0;
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryOptions { MaxRetryAttempts = 2, Delay = TimeSpan.Zero })
            .Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ =>
            {
                calls++;
                throw new InvalidOperationException("kalıcı");
            }));

        Assert.Equal("kalıcı", exception.Message);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Unhandled_exceptions_and_results_are_not_retried()
    {
        int calls = 0;
        ResiliencePipeline<int> pipeline = new ResiliencePipelineBuilder<int>()
            .AddRetry(new RetryOptions<int>
            {
                Delay = TimeSpan.Zero,
                ShouldHandle = new PredicateBuilder<int>().Handle<TimeoutException>().HandleResult(r => r < 0),
            })
            .Build();

        await Assert.ThrowsAsync<ArgumentException>(async () => await pipeline.ExecuteAsync(_ =>
        {
            calls++;
            throw new ArgumentException("geçersiz");
        }));
        Assert.Equal(1, calls);

        calls = 0;
        int result = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(++calls < 3 ? -1 : 7));
        Assert.Equal(7, result);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(DelayBackoffType.Constant, new[] { 100, 100, 100 })]
    [InlineData(DelayBackoffType.Linear, new[] { 100, 200, 300 })]
    [InlineData(DelayBackoffType.Exponential, new[] { 100, 200, 400 })]
    public void Backoff_types(DelayBackoffType type, int[] expectedMs)
    {
        var strategy = new RetryStrategy<object?>(new RetryOptions { Delay = TimeSpan.FromMilliseconds(100), BackoffType = type }, TimeProvider.System);

        Assert.Equal(expectedMs, Enumerable.Range(0, 3).Select(a => (int)strategy.CalculateDelay(a).TotalMilliseconds));
    }

    [Fact]
    public void Jitter_stays_within_25_percent_and_max_delay_caps()
    {
        var low = new RetryStrategy<object?>(new RetryOptions { Delay = TimeSpan.FromSeconds(1), UseJitter = true, Randomizer = () => 0 }, TimeProvider.System);
        var high = new RetryStrategy<object?>(new RetryOptions { Delay = TimeSpan.FromSeconds(1), UseJitter = true, Randomizer = () => 0.999999 }, TimeProvider.System);
        var capped = new RetryStrategy<object?>(
            new RetryOptions { Delay = TimeSpan.FromSeconds(1), BackoffType = DelayBackoffType.Exponential, MaxDelay = TimeSpan.FromSeconds(5) },
            TimeProvider.System
        );

        Assert.Equal(750, low.CalculateDelay(0).TotalMilliseconds, 1);
        Assert.Equal(1250, high.CalculateDelay(0).TotalMilliseconds, 1);
        Assert.Equal(TimeSpan.FromSeconds(5), capped.CalculateDelay(10));
        Assert.Equal(TimeSpan.FromDays(1), new RetryStrategy<object?>(new RetryOptions { BackoffType = DelayBackoffType.Exponential }, TimeProvider.System).CalculateDelay(5000));
    }

    [Fact]
    public async Task Discarded_results_are_disposed_and_delay_generator_wins()
    {
        var first = new DisposableResult();
        TimeSpan? usedDelay = null;
        int calls = 0;

        ResiliencePipeline<DisposableResult> pipeline = new ResiliencePipelineBuilder<DisposableResult>()
            .AddRetry(new RetryOptions<DisposableResult>
            {
                Delay = TimeSpan.FromHours(1), // DelayGenerator ezer
                ShouldHandle = o => o.Result?.Failed == true,
                DelayGenerator = _ => TimeSpan.FromMilliseconds(1),
                OnRetry = a =>
                {
                    usedDelay = a.RetryDelay;
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

        DisposableResult result = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(++calls == 1 ? first : new DisposableResult { Failed = false }));

        Assert.False(result.Failed);
        Assert.True(first.Disposed);
        Assert.Equal(TimeSpan.FromMilliseconds(1), usedDelay);
    }

    public sealed class DisposableResult : IDisposable
    {
        public bool Failed { get; init; } = true;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}

public class CircuitBreakerTests
{
    private static (ResiliencePipeline Pipeline, ManualClock Clock, CircuitBreakerStateProvider State, CircuitBreakerManualControl Control, List<string> Events) Create()
    {
        var clock = new ManualClock();
        var state = new CircuitBreakerStateProvider();
        var control = new CircuitBreakerManualControl();
        var events = new List<string>();

        ResiliencePipeline pipeline = new ResiliencePipelineBuilder { TimeProvider = clock }
            .AddCircuitBreaker(new CircuitBreakerOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 4,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(5),
                StateProvider = state,
                ManualControl = control,
                OnOpened = _ => Record(events, "opened"),
                OnHalfOpened = _ => Record(events, "half-open"),
                OnClosed = _ => Record(events, "closed"),
            })
            .Build();

        return (pipeline, clock, state, control, events);
    }

    private static ValueTask Record(List<string> events, string e)
    {
        events.Add(e);
        return ValueTask.CompletedTask;
    }

    private static ValueTask<int> Fail(CancellationToken _) => throw new HttpRequestException("503");

    private static ValueTask<int> Ok(CancellationToken _) => ValueTask.FromResult(1);

    [Fact]
    public async Task Opens_on_failure_ratio_rejects_then_half_opens_and_closes()
    {
        var (pipeline, clock, state, _, events) = Create();

        await pipeline.ExecuteAsync(Ok);
        await pipeline.ExecuteAsync(Ok);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync(Fail));
        Assert.Equal(CircuitState.Closed, state.CircuitState); // 3 istek: eşik (4) altında

        await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync(Fail)); // 2/4 = %50
        Assert.Equal(CircuitState.Open, state.CircuitState);

        int calls = 0;
        BrokenCircuitException broken = await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(++calls)));
        Assert.Equal(0, calls);
        Assert.IsType<HttpRequestException>(broken.InnerException);
        Assert.Equal(TimeSpan.FromSeconds(5), broken.RetryAfter);

        clock.Now += TimeSpan.FromSeconds(6);
        Assert.Equal(1, await pipeline.ExecuteAsync(Ok)); // yoklama başarılı
        Assert.Equal(CircuitState.Closed, state.CircuitState);
        Assert.Equal(new[] { "opened", "half-open", "closed" }, events);
    }

    [Fact]
    public async Task Failed_probe_reopens_the_circuit()
    {
        var (pipeline, clock, state, _, _) = Create();
        for (int i = 0; i < 4; i++)
            await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync(Fail));

        clock.Now += TimeSpan.FromSeconds(6);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync(Fail));

        Assert.Equal(CircuitState.Open, state.CircuitState);
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(Ok));
    }

    [Fact]
    public async Task Old_failures_slide_out_of_the_sampling_window()
    {
        var (pipeline, clock, state, _, _) = Create();
        await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync(Fail));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync(Fail));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync(Fail));

        clock.Now += TimeSpan.FromSeconds(11); // pencere dışına çıktılar
        await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync(Fail));

        Assert.Equal(CircuitState.Closed, state.CircuitState);
    }

    [Fact]
    public async Task Circuit_state_is_shared_across_result_types()
    {
        var (pipeline, _, state, _, _) = Create();

        for (int i = 0; i < 4; i++)
            await Assert.ThrowsAsync<HttpRequestException>(async () => await pipeline.ExecuteAsync<string>(_ => throw new HttpRequestException()));

        Assert.Equal(CircuitState.Open, state.CircuitState);

        // int dönen çağrı da aynı (açık) devreyi görür
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(Ok));
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task Manual_isolate_and_close()
    {
        var (pipeline, _, state, control, _) = Create();

        await control.IsolateAsync();
        Assert.Equal(CircuitState.Isolated, state.CircuitState);
        await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await pipeline.ExecuteAsync(Ok));

        await control.CloseAsync();
        Assert.Equal(1, await pipeline.ExecuteAsync(Ok));
    }
}

public class TimeoutLimiterFallbackHedgingTests
{
    [Fact]
    public async Task Timeout_cancels_and_throws_timeout_rejected()
    {
        bool timedOut = false;
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(new TimeoutOptions
            {
                Timeout = TimeSpan.FromMilliseconds(50),
                OnTimeout = _ =>
                {
                    timedOut = true;
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

        TimeoutRejectedException exception = await Assert.ThrowsAsync<TimeoutRejectedException>(async () =>
            await pipeline.ExecuteAsync(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return 1;
            }));

        Assert.True(timedOut);
        Assert.Equal(TimeSpan.FromMilliseconds(50), exception.Timeout);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_timeout()
    {
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(10)).Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return 1;
            }, cts.Token));
    }

    [Fact]
    public async Task Concurrency_limiter_rejects_when_full()
    {
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder().AddConcurrencyLimiter(permitLimit: 1).Build();
        var gate = new TaskCompletionSource();

        ValueTask<int> first = pipeline.ExecuteAsync(async _ =>
        {
            await gate.Task;
            return 1;
        });

        await Assert.ThrowsAsync<RateLimiterRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2)));

        gate.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(3, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(3)));
    }

    [Fact]
    public async Task Custom_rate_limiter_reports_retry_after()
    {
        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder().AddRateLimiter(limiter).Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        RateLimiterRejectedException rejected = await Assert.ThrowsAsync<RateLimiterRejectedException>(async () =>
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        Assert.NotNull(rejected.RetryAfter);
    }

    [Fact]
    public async Task Fallback_returns_substitute_on_failure()
    {
        ResiliencePipeline<string> pipeline = new ResiliencePipelineBuilder<string>()
            .AddFallback(new FallbackOptions<string>
            {
                ShouldHandle = new PredicateBuilder<string>().Handle<HttpRequestException>().HandleResult(r => r.Length == 0),
                FallbackAction = _ => ValueTask.FromResult(Outcome.FromResult("önbellekten")),
            })
            .Build();

        Assert.Equal("önbellekten", await pipeline.ExecuteAsync(_ => throw new HttpRequestException()));
        Assert.Equal("önbellekten", await pipeline.ExecuteAsync(_ => ValueTask.FromResult("")));
        Assert.Equal("canlı", await pipeline.ExecuteAsync(_ => ValueTask.FromResult("canlı")));
    }

    [Fact]
    public async Task Hedging_takes_the_fastest_and_cancels_the_rest()
    {
        bool slowCancelled = false;
        int attempts = 0;

        ResiliencePipeline<string> pipeline = new ResiliencePipelineBuilder<string>()
            .AddHedging(new HedgingOptions<string> { MaxHedgedAttempts = 1, Delay = TimeSpan.FromMilliseconds(50) })
            .Build();

        string result = await pipeline.ExecuteAsync(
            async context =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        slowCancelled = true;
                        throw;
                    }

                    return "yavaş";
                }

                return "hızlı";
            },
            new ResilienceContext()
        );

        Assert.Equal("hızlı", result);
        Assert.Equal(2, attempts);
        await Task.Delay(100);
        Assert.True(slowCancelled);
    }

    [Fact]
    public async Task Hedging_on_failure_starts_next_attempt_immediately()
    {
        int attempts = 0;
        ResiliencePipeline<int> pipeline = new ResiliencePipelineBuilder<int>()
            .AddHedging(new HedgingOptions<int> { MaxHedgedAttempts = 2, Delay = Timeout.InfiniteTimeSpan })
            .Build();

        int result = await pipeline.ExecuteAsync(_ => Interlocked.Increment(ref attempts) < 3 ? throw new HttpRequestException() : ValueTask.FromResult(9));

        Assert.Equal(9, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Strategies_compose_outer_to_inner()
    {
        int calls = 0;
        ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(5))                                              // toplam
            .AddRetry(new RetryOptions { MaxRetryAttempts = 2, Delay = TimeSpan.Zero })
            .AddTimeout(TimeSpan.FromMilliseconds(30))                                         // deneme başına
            .Build();

        // İlk iki deneme deneme-süresine takılır (TimeoutRejectedException retry edilir), üçüncüsü başarılı.
        int result = await pipeline.ExecuteAsync(async ct =>
        {
            if (++calls < 3)
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return calls;
        });

        Assert.Equal(3, result);
    }

    [Fact]
    public async Task Void_execution_and_empty_pipeline()
    {
        int calls = 0;
        await ResiliencePipeline.Empty.ExecuteAsync(_ =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });

        Assert.Equal(1, calls);
        Assert.Equal(5, ResiliencePipeline.Empty.Execute(_ => 5));
    }
}
