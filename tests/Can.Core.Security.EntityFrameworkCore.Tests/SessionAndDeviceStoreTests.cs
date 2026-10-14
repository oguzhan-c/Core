using System.Security.Cryptography;
using System.Text;
using Can.Core.Security.Devices;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Sessions;
using Can.Core.Security.Tokens;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Security.EntityFrameworkCore.Tests;

public sealed class SessionAndDeviceStoreTests : IAsyncLifetime
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingDenylist : IAccessTokenDenylist
    {
        public Dictionary<string, DateTimeOffset> Revoked { get; } = [];

        public Task RevokeAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        {
            Revoked[tokenId] = expiresAt;
            return Task.CompletedTask;
        }

        public ValueTask<bool> IsRevokedAsync(string tokenId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Revoked.ContainsKey(tokenId));
    }

    private sealed class RecordingSink : ISecurityEventSink
    {
        public List<SecurityEvent> Events { get; } = [];

        public ValueTask WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
        {
            lock (Events)
                Events.Add(securityEvent);
            return ValueTask.CompletedTask;
        }
    }

    private readonly RecordingSink _sink = new();
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Clock _clock = new();
    private readonly RecordingDenylist _denylist = new();
    private readonly AppUser _user = new("ayse@test.local", "Ayşe");
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IAccessTokenDenylist>(_denylist);
        services.AddSingleton(new JwtOptions { AccessTokenLifetime = TimeSpan.FromMinutes(15) });
        services.AddSingleton<IDeviceBindingService>(new DeviceBindingService("cihaz-baglama-icin-en-az-otuz-iki-karakterlik-anahtar", _clock));
        services.AddLogging();
        services.AddCanSecurityEvents(o => o.WriteToLog = false);
        services.AddSingleton<ISecurityEventSink>(_sink);
        services.AddCanUserSessions<AuthDbContext, Guid>();
        services.AddCanTrustedDevices<AuthDbContext, Guid>();
        _services = services.BuildServiceProvider();

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(_user);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private string UserId => _user.Id.ToString();

    /// <summary>Girişi taklit eder: yeni oturum.</summary>
    private async Task<RefreshToken<Guid>> LoginAsync(string? deviceId, string name)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var token = new RefreshToken<Guid>(_user.Id, new RefreshTokenValue("t", Guid.NewGuid().ToString("N"), _clock.Now.AddDays(7)), _clock.Now, "10.0.0.1", null,
            new SessionDevice(deviceId, name, "test-agent"));
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync();
        return token;
    }

    /// <summary>Token yenilemeyi taklit eder: zincirin yeni halkasını döner.</summary>
    private async Task<RefreshToken<Guid>> RefreshAsync(RefreshToken<Guid> current)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        RefreshToken<Guid> tracked = await db.RefreshTokens.SingleAsync(t => t.TokenHash == current.TokenHash);
        RefreshToken<Guid> next = tracked.Rotate(new RefreshTokenValue("n", Guid.NewGuid().ToString("N"), _clock.Now.AddDays(7)), _clock.Now, "10.0.0.2", null);
        db.RefreshTokens.Add(next);
        await db.SaveChangesAsync();
        return next;
    }

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    [Fact]
    public async Task Rotated_tokens_show_as_one_session_per_device()
    {
        RefreshToken<Guid> phone = await LoginAsync("phone-1", "iPhone");
        _clock.Now = _clock.Now.AddMinutes(5);
        RefreshToken<Guid> laptop = await LoginAsync(null, "Chrome · macOS");
        _clock.Now = _clock.Now.AddMinutes(5);
        RefreshToken<Guid> rotated = await RefreshAsync(await RefreshAsync(phone)); // iki yenileme: zincirde üç token, tek oturum
        Assert.Equal(phone.SessionId, rotated.SessionId);
        IReadOnlyList<UserSession> sessions = await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().ListAsync(UserId, laptop.SessionId));

        Assert.Equal(2, sessions.Count);
        UserSession phoneSession = sessions.Single(s => s.SessionId == phone.SessionId);
        Assert.Equal("iPhone", phoneSession.DeviceName);
        Assert.Equal("10.0.0.2", phoneSession.IpAddress);
        Assert.Equal(phone.SessionStartedAt, phoneSession.StartedAt);
        Assert.Equal(phone.SessionId, sessions[0].SessionId); // en son kullanılan önce
        Assert.True(sessions.Single(s => s.SessionId == laptop.SessionId).IsCurrent);
    }

    [Fact]
    public async Task Revoking_a_session_closes_its_tokens_and_blocks_its_access_tokens()
    {
        RefreshToken<Guid> phone = await LoginAsync("phone-1", "iPhone");
        RefreshToken<Guid> laptop = await LoginAsync(null, "Chrome");

        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().RevokeAsync(UserId, phone.SessionId)));
        Assert.False(await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().RevokeAsync(UserId, "olmayan")));
        Assert.False(await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().RevokeAsync(Guid.NewGuid().ToString(), laptop.SessionId))); // başkasının oturumu

        IReadOnlyList<UserSession> left = await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().ListAsync(UserId));
        Assert.Equal(laptop.SessionId, Assert.Single(left).SessionId);
        Assert.Equal(_clock.Now.AddMinutes(15), _denylist.Revoked[AccessTokenDenylistKeys.Session(phone.SessionId)]);
    }

    [Fact]
    public async Task Revoke_others_keeps_only_this_device()
    {
        RefreshToken<Guid> current = await LoginAsync(null, "Bu cihaz");
        await LoginAsync("phone-1", "iPhone");
        await LoginAsync("tablet-1", "iPad");

        Assert.Equal(2, await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().RevokeOthersAsync(UserId, current.SessionId)));
        Assert.Equal(current.SessionId, Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().ListAsync(UserId))).SessionId);

        Assert.Equal(1, await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().RevokeAllAsync(UserId)));
        Assert.Empty(await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().ListAsync(UserId)));
    }

    [Fact]
    public async Task Trusted_device_registers_verifies_once_and_revocation_ends_its_sessions()
    {
        using var deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(deviceKey.ExportSubjectPublicKeyInfo());
        string Sign(string challenge) => Convert.ToBase64String(deviceKey.SignData(Encoding.UTF8.GetBytes(challenge), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        TrustedDeviceInfo registered = await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().RegisterAsync(UserId, "phone-1", "Ayşe'nin iPhone'u", "ios", publicKey));
        Assert.Equal("Ayşe'nin iPhone'u", registered.Name);

        DeviceChallenge challenge = await InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<ITrustedDeviceService>().CreateChallenge("phone-1")));
        string signature = Sign(challenge.Challenge);
        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().VerifyAsync(UserId, "phone-1", challenge.Challenge, signature)));
        Assert.False(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().VerifyAsync(UserId, "phone-1", challenge.Challenge, signature))); // tekrar
        Assert.False(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().VerifyAsync(Guid.NewGuid().ToString(), "phone-1", challenge.Challenge, signature))); // başka kullanıcı

        Assert.NotNull(Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().ListAsync(UserId))).LastUsedAt);

        RefreshToken<Guid> phoneSession = await LoginAsync("phone-1", "iPhone");
        RefreshToken<Guid> laptopSession = await LoginAsync(null, "Chrome");
        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().RevokeAsync(UserId, "phone-1")));

        Assert.Empty(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().ListAsync(UserId)));
        Assert.Equal(laptopSession.SessionId, Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().ListAsync(UserId))).SessionId);
        Assert.True(_denylist.Revoked.ContainsKey(AccessTokenDenylistKeys.Session(phoneSession.SessionId)));

        DeviceChallenge after = await InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<ITrustedDeviceService>().CreateChallenge("phone-1")));
        Assert.False(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().VerifyAsync(UserId, "phone-1", after.Challenge, Sign(after.Challenge)))); // kaldırılmış
    }

    [Fact]
    public async Task Reinstalled_app_replaces_the_key()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().RegisterAsync(UserId, "phone-1", "iPhone", "ios", Convert.ToBase64String(oldKey.ExportSubjectPublicKeyInfo())));
        await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().RegisterAsync(UserId, "phone-1", "iPhone", "ios", Convert.ToBase64String(newKey.ExportSubjectPublicKeyInfo())));

        DeviceChallenge challenge = await InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<ITrustedDeviceService>().CreateChallenge("phone-1")));
        string Sign(ECDsa key) => Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(challenge.Challenge), HashAlgorithmName.SHA256));

        Assert.False(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().VerifyAsync(UserId, "phone-1", challenge.Challenge, Sign(oldKey))));
        Assert.True(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().VerifyAsync(UserId, "phone-1", challenge.Challenge, Sign(newKey))));
        Assert.Single(await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().ListAsync(UserId)));
    }

    [Fact]
    public async Task Session_and_device_changes_are_published_as_security_events()
    {
        RefreshToken<Guid> phone = await LoginAsync("phone-1", "iPhone");
        await LoginAsync(null, "Chrome");
        await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().RevokeAsync(UserId, phone.SessionId));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().RegisterAsync(UserId, "phone-2", "Pixel", "android", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        DeviceChallenge challenge = await InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<ITrustedDeviceService>().CreateChallenge("phone-2")));
        await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().VerifyAsync(UserId, "phone-2", challenge.Challenge, "AAAA")); // bozuk imza
        await InScopeAsync(sp => sp.GetRequiredService<IUserSessionService>().RevokeAllAsync(UserId));

        Assert.Equal(
            new[] { SecurityEventTypes.SessionRevoked, SecurityEventTypes.DeviceRegistered, SecurityEventTypes.DeviceVerificationFailed, SecurityEventTypes.AllSessionsRevoked },
            _sink.Events.Select(e => e.Type));
        Assert.All(_sink.Events, e => Assert.Equal(UserId, e.UserId));
        Assert.Equal(phone.SessionId, _sink.Events[0].Properties["revoked_session"]);
        Assert.Equal("phone-2", _sink.Events[2].DeviceId);
        Assert.Equal(SecurityEventSeverity.Warning, _sink.Events[2].Severity);
    }

    [Fact]
    public async Task Known_devices_are_recognised_by_sessions_and_trusted_devices()
    {
        await LoginAsync("phone-1", "iPhone");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await InScopeAsync(sp => sp.GetRequiredService<ITrustedDeviceService>().RegisterAsync(UserId, "tablet-1", "iPad", "ios", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));

        IUserSessionService Sessions(IServiceProvider sp) => sp.GetRequiredService<IUserSessionService>();
        Assert.True(await InScopeAsync(sp => Sessions(sp).IsKnownDeviceAsync(UserId, "phone-1")));
        Assert.True(await InScopeAsync(sp => Sessions(sp).IsKnownDeviceAsync(UserId, "tablet-1")));
        Assert.False(await InScopeAsync(sp => Sessions(sp).IsKnownDeviceAsync(UserId, "yeni-telefon")));
        Assert.False(await InScopeAsync(sp => Sessions(sp).IsKnownDeviceAsync(Guid.NewGuid().ToString(), "phone-1"))); // başkasının cihazı
    }
}

public sealed class EventLogDbContext(DbContextOptions<EventLogDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyCanSecurityEventLog(o => o.TablePrefix = "Auth");
}

public sealed class SecurityEventStoreTests
{
    [Fact]
    public async Task Events_are_written_in_their_own_scope_and_queryable_per_user()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<EventLogDbContext>(o => o.UseSqlite(connection));
        services.AddCanSecurityEvents(o => o.WriteToLog = false);
        services.AddCanSecurityEventStore<EventLogDbContext>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            EventLogDbContext db = scope.ServiceProvider.GetRequiredService<EventLogDbContext>();
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            ISecurityEvents events = scope.ServiceProvider.GetRequiredService<ISecurityEvents>();
            await events.PublishAsync(new SecurityEvent(SecurityEventTypes.LoginFailed, "42") { Reason = "invalid_password", IpAddress = "10.0.0.1" }, TestContext.Current.CancellationToken);
            await events.PublishAsync(new SecurityEvent(SecurityEventTypes.LoginSucceeded, "42") { Properties = new Dictionary<string, string?> { ["method"] = "passkey" } }, TestContext.Current.CancellationToken);
            await events.PublishAsync(new SecurityEvent(SecurityEventTypes.LoginSucceeded, "7"), TestContext.Current.CancellationToken);

            Assert.False(db.ChangeTracker.HasChanges()); // isteğin DbContext'i kullanılmadı
        }

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            EventLogDbContext db = scope.ServiceProvider.GetRequiredService<EventLogDbContext>();
            List<SecurityEventRecord> mine = await db.Set<SecurityEventRecord>().Where(e => e.UserId == "42").ToListAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, mine.Count);
            SecurityEventRecord failed = mine.Single(e => e.Type == SecurityEventTypes.LoginFailed);
            Assert.Equal(SecurityEventSeverity.Warning, failed.Severity);
            Assert.Equal("10.0.0.1", failed.IpAddress);
            Assert.Equal("invalid_password", failed.Reason);
            Assert.Equal("{\"method\":\"passkey\"}", mine.Single(e => e.Type == SecurityEventTypes.LoginSucceeded).Properties);
        }
    }
}
