using System.Diagnostics;
using Can.Core.Observability.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Observability.Tests;

public class OpenTelemetryTests
{
    [Fact]
    public void Registers_tracer_and_meter_providers_listening_to_can_sources()
    {
        var services = new ServiceCollection();
        services.AddCanOpenTelemetry(o =>
        {
            o.ServiceName = "test";
            o.OtlpEndpoint = new Uri("http://127.0.0.1:1"); // kapatırken yapılan son gönderim hemen reddedilsin
            o.AdditionalSources.Add("Npgsql");
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<global::OpenTelemetry.Trace.TracerProvider>());
        Assert.NotNull(provider.GetService<global::OpenTelemetry.Metrics.MeterProvider>());

        using var source = new ActivitySource("Can.Core.Mailing");
        Assert.True(source.HasListeners());
    }

    [Fact]
    public void Service_name_is_required()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddCanOpenTelemetry(_ => { }));
    }
}
