using CanFx.Security.DependencyInjection;
using CanFx.Security.Lockout;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Security.Tests;

public sealed class LoginThrottleTests
{
    private sealed class MovingClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CancellationToken None => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Failures_block_only_the_ip_that_made_them()
    {
        var clock = new MovingClock();
        var throttle = new InMemoryLoginThrottle(new LoginThrottleOptions { MaxAttemptsPerIp = 3 }, clock);

        Assert.False(await throttle.RegisterFailureAsync("ali@example.com", "10.0.0.1", cancellationToken: None));
        Assert.False(await throttle.RegisterFailureAsync("ALI@example.com ", "10.0.0.1", cancellationToken: None)); // e-posta normalize edilir
        Assert.False(await throttle.IsBlockedAsync("ali@example.com", "10.0.0.1", None));
        Assert.True(await throttle.RegisterFailureAsync("ali@example.com", "10.0.0.1", cancellationToken: None));

        Assert.True(await throttle.IsBlockedAsync("ali@example.com", "10.0.0.1", None));
        Assert.False(await throttle.IsBlockedAsync("ali@example.com", "10.0.0.2", None));    // hesabın sahibi başka IP'den girebilir
        Assert.False(await throttle.IsBlockedAsync("veli@example.com", "10.0.0.1", None));   // aynı IP, başka hesap

        clock.Now += TimeSpan.FromMinutes(16);
        Assert.False(await throttle.IsBlockedAsync("ali@example.com", "10.0.0.1", None));    // süre doldu
    }

    [Fact]
    public async Task Counter_expires_after_the_window_and_reset_clears_it()
    {
        var clock = new MovingClock();
        var throttle = new InMemoryLoginThrottle(new LoginThrottleOptions { MaxAttemptsPerIp = 2, Window = TimeSpan.FromMinutes(5) }, clock);

        await throttle.RegisterFailureAsync("ali@example.com", "10.0.0.1", cancellationToken: None);
        clock.Now += TimeSpan.FromMinutes(6);
        Assert.False(await throttle.RegisterFailureAsync("ali@example.com", "10.0.0.1", cancellationToken: None)); // eski deneme sayılmaz

        await throttle.ResetAsync("ali@example.com", "10.0.0.1", None);                                              // başarılı giriş
        Assert.False(await throttle.RegisterFailureAsync("ali@example.com", "10.0.0.1", cancellationToken: None));

        // Çağrıya özel sınır (ör. ayarlardan) ve bilinmeyen IP tek kovada.
        var strict = new LoginThrottleLimits(1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        Assert.True(await throttle.RegisterFailureAsync("veli@example.com", null, strict, None));
        Assert.True(await throttle.IsBlockedAsync("veli@example.com", " ", None));
    }

    [Fact]
    public void Security_registration_provides_the_throttle_and_validates_limits()
    {
        var services = new ServiceCollection();
        services.AddCanSecurity(o =>
        {
            o.Jwt.Issuer = "test";
            o.Jwt.Audience = "test";
            o.Jwt.SigningKey = new string('k', 32);
            o.LoginThrottle.AccountMaxAttempts = 30;
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryLoginThrottle>(provider.GetRequiredService<ILoginThrottle>());
        Assert.Equal(30, provider.GetRequiredService<LoginThrottleOptions>().AccountMaxAttempts);

        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddCanSecurity(o =>
        {
            o.Jwt.Issuer = "test";
            o.Jwt.Audience = "test";
            o.Jwt.SigningKey = new string('k', 32);
            o.LoginThrottle.MaxAttemptsPerIp = 0;
        }));
    }
}
