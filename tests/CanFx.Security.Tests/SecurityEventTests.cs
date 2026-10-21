using System.Collections.Concurrent;
using CanFx.Security.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CanFx.Security.Tests;

public class SecurityEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private sealed class Recorder : ISecurityEventSink
    {
        public ConcurrentQueue<SecurityEvent> Events { get; } = new();

        public ValueTask WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(securityEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Broken : ISecurityEventSink
    {
        public ValueTask WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SIEM kapalı");
    }

    private sealed class AddIp : ISecurityEventEnricher
    {
        public SecurityEvent Enrich(SecurityEvent securityEvent) => securityEvent with { IpAddress = securityEvent.IpAddress ?? "10.0.0.9" };
    }

    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger(Logs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    private static (ServiceProvider Services, Recorder Recorder, Logs Logs) Create()
    {
        var recorder = new Recorder();
        var logs = new Logs();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddSingleton<TimeProvider>(new FixedClock(Now));
        services.AddCanSecurityEvents();
        services.AddSingleton<ISecurityEventSink, Broken>();      // bozuk sink diğerlerini durdurmaz
        services.AddSingleton<ISecurityEventSink>(recorder);
        services.AddSingleton<ISecurityEventEnricher, AddIp>();
        return (services.BuildServiceProvider(), recorder, logs);
    }

    [Fact]
    public async Task Events_are_enriched_timestamped_and_reach_every_working_sink()
    {
        (ServiceProvider services, Recorder recorder, Logs logs) = Create();
        await using (services)
        {
            using IServiceScope scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISecurityEvents>()
                .PublishAsync(new SecurityEvent(SecurityEventTypes.LoginFailed, "42") { Reason = "invalid_password" }, TestContext.Current.CancellationToken);

            SecurityEvent written = Assert.Single(recorder.Events);
            Assert.Equal(Now, written.OccurredAt);
            Assert.Equal("10.0.0.9", written.IpAddress);
            Assert.Equal(SecurityEventSeverity.Warning, written.Severity);

            // log sink: olay uyarı düzeyinde; bozuk sink hata olarak loglandı
            Assert.Contains(logs.Entries, e => e.Category == "CanFx.Security.Events" && e.Level == LogLevel.Warning
                && e.Message.Contains("login.failed", StringComparison.Ordinal) && e.Message.Contains("invalid_password", StringComparison.Ordinal));
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Broken", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(SecurityEventTypes.LoginSucceeded, SecurityEventSeverity.Info)]
    [InlineData(SecurityEventTypes.AccountLocked, SecurityEventSeverity.Warning)]
    [InlineData(SecurityEventTypes.RefreshTokenReused, SecurityEventSeverity.Critical)]
    [InlineData("payment.approved", SecurityEventSeverity.Info)]
    public void Severity_defaults_by_type(string type, SecurityEventSeverity expected)
    {
        Assert.Equal(expected, new SecurityEvent(type).Severity);
    }

    [Fact]
    public void Log_sink_can_be_turned_off()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCanSecurityEvents(o => o.WriteToLog = false);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<ISecurityEventSink>());
    }
}
