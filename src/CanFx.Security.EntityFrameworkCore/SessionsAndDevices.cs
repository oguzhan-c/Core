using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using CanFx.Security.Devices;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Sessions;
using CanFx.Security.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CanFx.Security.EntityFrameworkCore;

/// <summary>
/// <see cref="IUserSessionService"/>: oturumlar <c>RefreshTokens</c> tablosundan okunur (oturum = aynı
/// <c>SessionId</c>'yi taşıyan token zinciri). Değişiklikler hemen kaydedilir.
/// </summary>
/// <remarks>
/// Bu sürümden önce oluşturulmuş token'ların oturum kimliği yoktur: listede görünmezler, <see cref="RevokeAllAsync"/> ile
/// kapanırlar (ya da süreleri dolunca).
/// </remarks>
public sealed class EfUserSessionService<TContext, TId> : IUserSessionService
    where TContext : DbContext
    where TId : notnull, IEquatable<TId>
{
    /// <summary>Tek oturum kapatıldığında yazılan iptal nedeni.</summary>
    public const string RevokedReason = "session_revoked";

    private readonly TContext _context;
    private readonly SessionRevoker<TId> _revoker;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceProvider _services;

    public EfUserSessionService(TContext context, TimeProvider timeProvider, IServiceProvider services)
    {
        _context = context;
        _timeProvider = timeProvider;
        _services = services;
        _revoker = new SessionRevoker<TId>(context, timeProvider, services);
    }

    public async Task<bool> IsKnownDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !IdParser<TId>.TryParse(userId, out TId? id))
            return false;

        bool seen = await _context.Set<RefreshToken<TId>>().AsNoTracking()
            .Where(IdParser<TId>.UserIs<RefreshToken<TId>>(id!))
            .AnyAsync(t => t.DeviceId == deviceId, cancellationToken).ConfigureAwait(false);
        if (seen || _context.Model.FindEntityType(typeof(TrustedDevice<TId>)) is null)
            return seen;

        return await _context.Set<TrustedDevice<TId>>().AsNoTracking()
            .Where(IdParser<TId>.UserIs<TrustedDevice<TId>>(id!))
            .AnyAsync(d => d.DeviceId == deviceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserSession>> ListAsync(string userId, string? currentSessionId = null, CancellationToken cancellationToken = default)
    {
        if (!IdParser<TId>.TryParse(userId, out TId? id))
            return [];

        DateTimeOffset now = _timeProvider.GetUtcNow();
        // Tarih karşılaştırması bellekte: SQLite DateTimeOffset karşılaştıramaz; kullanıcı başına kayıt sayısı küçüktür.
        List<RefreshToken<TId>> open = await _context.Set<RefreshToken<TId>>().AsNoTracking()
            .Where(IdParser<TId>.UserIs<RefreshToken<TId>>(id!))
            .Where(t => t.RevokedAt == null && t.SessionId != "")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return open
            .Where(t => t.IsActive(now))
            .GroupBy(t => t.SessionId)
            .Select(g => g.OrderByDescending(t => t.CreatedAt).First())
            .OrderByDescending(t => t.LastUsedAt ?? t.CreatedAt)
            .Select(t => new UserSession(
                t.SessionId,
                t.DeviceId,
                t.DeviceName,
                t.UserAgent,
                t.LastUsedIp ?? t.CreatedByIp,
                t.SessionStartedAt,
                t.LastUsedAt,
                t.ExpiresAt,
                string.Equals(t.SessionId, currentSessionId, StringComparison.Ordinal)))
            .ToList();
    }

    public async Task<bool> RevokeAsync(string userId, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        bool revoked = await _revoker.RevokeAsync(userId, t => t.SessionId == sessionId, RevokedReason, cancellationToken).ConfigureAwait(false) > 0;
        if (revoked)
            await PublishAsync(new SecurityEvent(SecurityEventTypes.SessionRevoked, userId) { Properties = Props("revoked_session", sessionId) }, cancellationToken).ConfigureAwait(false);
        return revoked;
    }

    public async Task<int> RevokeOthersAsync(string userId, string currentSessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSessionId);
        int count = await _revoker.RevokeAsync(userId, t => t.SessionId != currentSessionId, RevokedReason, cancellationToken).ConfigureAwait(false);
        if (count > 0)
            await PublishAsync(new SecurityEvent(SecurityEventTypes.OtherSessionsRevoked, userId) { Properties = Props("count", count.ToString(CultureInfo.InvariantCulture)) }, cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task<int> RevokeAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        int count = await _revoker.RevokeAsync(userId, _ => true, RevokedReason, cancellationToken).ConfigureAwait(false);
        await PublishAsync(new SecurityEvent(SecurityEventTypes.AllSessionsRevoked, userId) { Properties = Props("count", count.ToString(CultureInfo.InvariantCulture)) }, cancellationToken).ConfigureAwait(false);
        return count;
    }

    private ValueTask PublishAsync(SecurityEvent securityEvent, CancellationToken cancellationToken) =>
        _services.GetService<ISecurityEvents>()?.PublishAsync(securityEvent, cancellationToken) ?? ValueTask.CompletedTask;

    private static Dictionary<string, string?> Props(string key, string value) => new() { [key] = value };
}

/// <summary>
/// <see cref="ITrustedDeviceService"/>: cihazlar <c>TrustedDevices</c> tablosunda. Değişiklikler hemen kaydedilir.
/// </summary>
public sealed class EfTrustedDeviceService<TContext, TId> : ITrustedDeviceService
    where TContext : DbContext
    where TId : notnull, IEquatable<TId>
{
    /// <summary>Cihaz kaldırıldığında oturumlarına yazılan iptal nedeni.</summary>
    public const string RevokedReason = "device_revoked";

    private readonly TContext _context;
    private readonly IDeviceBindingService _binding;
    private readonly TimeProvider _timeProvider;
    private readonly SessionRevoker<TId> _revoker;
    private readonly IServiceProvider _services;

    public EfTrustedDeviceService(TContext context, IDeviceBindingService binding, TimeProvider timeProvider, IServiceProvider services)
    {
        _context = context;
        _binding = binding;
        _timeProvider = timeProvider;
        _services = services;
        _revoker = new SessionRevoker<TId>(context, timeProvider, services);
    }

    private ValueTask PublishAsync(string type, string userId, string deviceId, string? reason, CancellationToken cancellationToken) =>
        _services.GetService<ISecurityEvents>()?.PublishAsync(new SecurityEvent(type, userId) { DeviceId = deviceId, Reason = reason }, cancellationToken)
        ?? ValueTask.CompletedTask;

    public async Task<TrustedDeviceInfo> RegisterAsync(string userId, string deviceId, string name, string? platform, string publicKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!IdParser<TId>.TryParse(userId, out TId? id))
            throw new ArgumentException("Kullanıcı kimliği geçersiz.", nameof(userId));

        byte[] key = _binding.ParsePublicKey(publicKey);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        TrustedDevice<TId>? device = await FindAsync(id!, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            device = new TrustedDevice<TId>(id!, deviceId, name, platform, key, now);
            _context.Add(device);
        }
        else
        {
            device.Replace(name, platform, key, now);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync(SecurityEventTypes.DeviceRegistered, userId, deviceId, null, cancellationToken).ConfigureAwait(false);
        return Info(device);
    }

    public async Task<IReadOnlyList<TrustedDeviceInfo>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!IdParser<TId>.TryParse(userId, out TId? id))
            return [];

        List<TrustedDevice<TId>> devices = await _context.Set<TrustedDevice<TId>>().AsNoTracking()
            .Where(IdParser<TId>.UserIs<TrustedDevice<TId>>(id!))
            .Where(d => d.RevokedAt == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return devices.OrderByDescending(d => d.LastUsedAt ?? d.CreatedAt).Select(Info).ToList();
    }

    public DeviceChallenge CreateChallenge(string deviceId) => _binding.CreateChallenge(deviceId);

    public async Task<bool> VerifyAsync(string userId, string deviceId, string challenge, string signature, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !IdParser<TId>.TryParse(userId, out TId? id))
            return false;

        TrustedDevice<TId>? device = await FindAsync(id!, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is not { IsActive: true })
        {
            await PublishAsync(SecurityEventTypes.DeviceVerificationFailed, userId, deviceId, "unknown_device", cancellationToken).ConfigureAwait(false);
            return false;
        }

        DeviceVerification result = _binding.Verify(device.PublicKey, deviceId, challenge, signature, device.LastChallengeIssuedAt);
        if (!result.IsValid)
        {
            await PublishAsync(SecurityEventTypes.DeviceVerificationFailed, userId, deviceId, result.Failure, cancellationToken).ConfigureAwait(false);
            return false;
        }

        device.MarkVerified(result.IssuedAt, _timeProvider.GetUtcNow());
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Aynı meydan okumayla eşzamanlı ikinci doğrulama: ilki kazandı, bu tekrar sayılır.
            await PublishAsync(SecurityEventTypes.DeviceVerificationFailed, userId, deviceId, "replayed", cancellationToken).ConfigureAwait(false);
            return false;
        }

        await PublishAsync(SecurityEventTypes.DeviceVerified, userId, deviceId, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RevokeAsync(string userId, string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !IdParser<TId>.TryParse(userId, out TId? id))
            return false;

        TrustedDevice<TId>? device = await FindAsync(id!, deviceId, cancellationToken).ConfigureAwait(false);
        if (device is not { IsActive: true })
            return false;

        device.Revoke(_timeProvider.GetUtcNow());
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _revoker.RevokeAsync(userId, t => t.DeviceId == deviceId, RevokedReason, cancellationToken).ConfigureAwait(false);
        await PublishAsync(SecurityEventTypes.DeviceRevoked, userId, deviceId, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Task<TrustedDevice<TId>?> FindAsync(TId userId, string deviceId, CancellationToken cancellationToken) =>
        _context.Set<TrustedDevice<TId>>()
            .Where(IdParser<TId>.UserIs<TrustedDevice<TId>>(userId))
            .FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);

    private static TrustedDeviceInfo Info(TrustedDevice<TId> device) =>
        new(device.DeviceId, device.Name, device.Platform, device.CreatedAt, device.LastUsedAt);
}

/// <summary>Oturum token'larını iptal eder ve access token'larını (<c>sid</c>) engel listesine yazar.</summary>
internal sealed class SessionRevoker<TId>(DbContext context, TimeProvider timeProvider, IServiceProvider services)
    where TId : notnull, IEquatable<TId>
{
    public async Task<int> RevokeAsync(string userId, Expression<Func<RefreshToken<TId>, bool>> filter, string reason, CancellationToken cancellationToken)
    {
        if (!IdParser<TId>.TryParse(userId, out TId? id))
            return 0;

        List<RefreshToken<TId>> tokens = await context.Set<RefreshToken<TId>>()
            .Where(IdParser<TId>.UserIs<RefreshToken<TId>>(id!))
            .Where(t => t.RevokedAt == null)
            .Where(filter)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (tokens.Count == 0)
            return 0;

        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (RefreshToken<TId> token in tokens)
            token.Revoke(now, ip: null, reason);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Access token'lar: oturumun en uzun ömürlü token'ı en geç şimdi + AccessTokenLifetime'da biter.
        if (services.GetService<IAccessTokenDenylist>() is { } denylist)
        {
            TimeSpan lifetime = services.GetService<JwtOptions>()?.AccessTokenLifetime ?? TimeSpan.FromHours(1);
            foreach (string sessionId in tokens.Select(t => t.SessionId).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal))
                await denylist.RevokeAsync(AccessTokenDenylistKeys.Session(sessionId), now + lifetime, cancellationToken).ConfigureAwait(false);
        }

        return tokens.Select(t => t.SessionId).Distinct(StringComparer.Ordinal).Count();
    }
}

/// <summary>Metin kullanıcı kimliğini <typeparamref name="TId"/>'ye çevirir; EF'e çevrilebilir eşitlik koşulu kurar.</summary>
internal static class IdParser<TId>
    where TId : notnull, IEquatable<TId>
{
    private static readonly TypeConverter Converter = TypeDescriptor.GetConverter(typeof(TId));

    public static bool TryParse(string? value, out TId? id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            id = (TId?)Converter.ConvertFromString(null, CultureInfo.InvariantCulture, value);
            return id is not null;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary><c>x =&gt; x.UserId == id</c> (generic tipte <c>==</c> yazılamadığı için ifade ağacıyla).</summary>
    public static Expression<Func<T, bool>> UserIs<T>(TId id)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(T), "x");
        return Expression.Lambda<Func<T, bool>>(
            Expression.Equal(Expression.Property(parameter, "UserId"), Expression.Constant(id, typeof(TId))),
            parameter);
    }
}

public static class SessionAndDeviceServiceCollectionExtensions
{
    /// <summary>
    /// "Aktif oturumlarım" (<see cref="IUserSessionService"/>). Kapatılan oturumların access token'larının da hemen
    /// reddedilmesi için WebApi'de <c>AddCanAccessTokenRevocation()</c> + <c>UseCanAccessTokenRevocation()</c>.
    /// </summary>
    public static IServiceCollection AddCanUserSessions<TContext, TId>(this IServiceCollection services)
        where TContext : DbContext
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<IUserSessionService>();
        services.AddScoped<IUserSessionService, EfUserSessionService<TContext, TId>>();
        return services;
    }

    /// <summary>
    /// Kayıtlı cihazlar (<see cref="ITrustedDeviceService"/>). <c>AddCanSecurity</c>'de <c>VerificationCodeKey</c> verilmiş
    /// olmalı (meydan okumalar onunla imzalanır).
    /// </summary>
    public static IServiceCollection AddCanTrustedDevices<TContext, TId>(this IServiceCollection services)
        where TContext : DbContext
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<ITrustedDeviceService>();
        services.AddScoped<ITrustedDeviceService>(sp => new EfTrustedDeviceService<TContext, TId>(
            sp.GetRequiredService<TContext>(),
            sp.GetService<IDeviceBindingService>()
                ?? throw new InvalidOperationException("Kayıtlı cihazlar için AddCanSecurity'de VerificationCodeKey verilmeli."),
            sp.GetRequiredService<TimeProvider>(),
            sp));
        return services;
    }
}
