using Can.Core.Application.DependencyInjection;
using Can.Core.Application.Exceptions;
using Can.Core.Application.Features;
using Can.Core.Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Application.Tests;

public sealed record BetaReportQuery : IRequest<string>, IFeatureGatedRequest
{
    public IReadOnlyCollection<string> RequiredFeatures => ["beta-reports"];
}

public sealed class BetaReportQueryHandler : IRequestHandler<BetaReportQuery, string>
{
    public Task<string> Handle(BetaReportQuery request, CancellationToken cancellationToken) => Task.FromResult("rapor");
}

public class FeatureFlagTests
{
    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Host(Action<IServiceCollection> features)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICurrentUser>(User);
            services.AddSingleton<ICurrentTenant>(Tenant);
            services.AddCanApplication(typeof(FeatureFlagTests).Assembly);
            features(services);
            _provider = services.BuildServiceProvider();
        }

        public FakeCurrentUser User { get; } = new();

        public FakeCurrentTenant Tenant { get; } = new();

        public async Task<bool> IsEnabledAsync(string feature)
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IFeatureFlags>().IsEnabledAsync(feature);
        }

        public async Task<IReadOnlyList<string>> EnabledAsync()
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IFeatureFlags>().GetEnabledAsync();
        }

        public async Task<string> SendAsync<T>(T request)
            where T : IRequest<string>
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
        }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    [Fact]
    public async Task Rules_target_users_roles_and_tenants()
    {
        await using var host = new Host(s => s.AddCanFeatureFlags(o =>
        {
            o.Features["everyone"] = new FeatureDefinition { Enabled = true, ExcludedTenants = { "globex" } };
            o.Features["for-acme"] = new FeatureDefinition { Tenants = { "acme" } };
            o.Features["for-ali"] = new FeatureDefinition { Users = { "ali" } };
            o.Features["for-testers"] = new FeatureDefinition { Roles = { "Tester" } };
            o.Features["off"] = new FeatureDefinition();
        }));

        host.User.Id = "veli";
        host.Tenant.Id = "initech";
        Assert.Equal(new[] { "everyone" }, await host.EnabledAsync());
        Assert.False(await host.IsEnabledAsync("unknown"));

        host.Tenant.Id = "ACME"; // büyük/küçük harf fark etmez
        Assert.True(await host.IsEnabledAsync("FOR-ACME")); // ad da büyük/küçük harfe duyarsız

        host.User.Id = "ali";
        host.User.RoleList.Add("tester");
        Assert.Equal(new[] { "everyone", "for-acme", "for-ali", "for-testers" }, await host.EnabledAsync());

        host.Tenant.Id = "globex"; // hariç tutulan tenant: herkese açık özellik bile kapalı
        Assert.False(await host.IsEnabledAsync("everyone"));
    }

    [Fact]
    public async Task Rollout_is_sticky_and_roughly_proportional()
    {
        await using var host = new Host(s => s.AddCanFeatureFlags(o =>
        {
            o.Features["half"] = new FeatureDefinition { RolloutPercentage = 50 };
            o.Features["none"] = new FeatureDefinition { RolloutPercentage = 0 };
            o.Features["all"] = new FeatureDefinition { RolloutPercentage = 100 };
        }));

        int enabled = 0;
        for (int i = 0; i < 1000; i++)
        {
            host.User.Id = $"user-{i}";
            bool first = await host.IsEnabledAsync("half");
            Assert.Equal(first, await host.IsEnabledAsync("half")); // aynı kullanıcı hep aynı sonucu alır
            Assert.False(await host.IsEnabledAsync("none"));
            Assert.True(await host.IsEnabledAsync("all"));
            if (first)
                enabled++;
        }

        Assert.InRange(enabled, 420, 580);

        host.User.Id = null; // anonim ve tenant'sız: yüzdeye girmez
        Assert.False(await host.IsEnabledAsync("all"));
    }

    [Fact]
    public async Task Time_window_limits_every_rule()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using var host = new Host(s => s.AddCanFeatureFlags(o =>
        {
            o.Features["upcoming"] = new FeatureDefinition { Enabled = true, EnabledFrom = now.AddDays(1) };
            o.Features["expired"] = new FeatureDefinition { Users = { "user-1" }, EnabledUntil = now.AddDays(-1) };
            o.Features["campaign"] = new FeatureDefinition { Enabled = true, EnabledFrom = now.AddDays(-1), EnabledUntil = now.AddDays(1) };
        }));

        Assert.Equal(new[] { "campaign" }, await host.EnabledAsync());
    }

    [Fact]
    public async Task Configuration_supports_shorthand_and_reload()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:dark-mode"] = "true",
                ["Features:legacy"] = "false",
                ["Features:new-checkout:Tenants:0"] = "acme",
                ["Features:new-checkout:RolloutPercentage"] = "0",
            })
            .Build();
        await using var host = new Host(s => s.AddCanFeatureFlags(configuration.GetSection("Features")));
        host.Tenant.Id = "acme";

        Assert.Equal(new[] { "dark-mode", "new-checkout" }, await host.EnabledAsync());

        configuration["Features:legacy"] = "true";
        configuration.Reload(); // dosya değişince olduğu gibi
        Assert.True(await host.IsEnabledAsync("legacy"));
    }

    [Fact]
    public async Task Gated_request_is_hidden_when_the_feature_is_off()
    {
        await using var host = new Host(s => s.AddCanFeatureFlags(o => o.Features["beta-reports"] = new FeatureDefinition { Users = { "ali" } }));

        host.User.Id = "veli";
        NotFoundException hidden = await Assert.ThrowsAsync<NotFoundException>(() => host.SendAsync(new BetaReportQuery()));
        Assert.Equal("feature_disabled", hidden.Code);

        host.User.Id = "ali";
        Assert.Equal("rapor", await host.SendAsync(new BetaReportQuery()));
    }

    [Fact]
    public async Task Without_configuration_every_feature_is_off()
    {
        await using var host = new Host(_ => { });

        Assert.Empty(await host.EnabledAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => host.SendAsync(new BetaReportQuery()));
    }
}
