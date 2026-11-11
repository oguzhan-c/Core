using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CanFx.Security.Lockout;

/// <summary>Hesap + IP başına hatalı giriş sınırı.</summary>
/// <param name="MaxAttempts">Bu kadar hatalı denemeden sonra o IP, o hesap için engellenir.</param>
/// <param name="Window">Denemelerin sayıldığı süre (son denemeden bu kadar sonra sayaç sıfırlanır).</param>
/// <param name="LockoutDuration">Engelin süresi.</param>
public sealed record LoginThrottleLimits(int MaxAttempts, TimeSpan Window, TimeSpan LockoutDuration)
{
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LockoutDuration, TimeSpan.Zero);
    }
}

/// <summary>Hatalı giriş sınırlarının varsayılanları (<c>AddCanSecurity(o =&gt; o.LoginThrottle...)</c>).</summary>
public sealed class LoginThrottleOptions
{
    /// <summary>Aynı IP'den aynı hesaba en fazla hatalı deneme (varsayılan 5).</summary>
    public int MaxAttemptsPerIp { get; set; } = 5;

    /// <summary>Denemelerin sayıldığı süre (varsayılan 15 dk).</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>IP engelinin süresi (varsayılan 15 dk).</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Hesabın TÜM IP'lerden toplam hatalı deneme sınırı (varsayılan 20): dağıtık (çok IP'li) kaba kuvvete karşı.
    /// <c>user.RegisterFailedAccess(now, options.AccountMaxAttempts)</c> ile kullanılır; ulaşılınca hesap herkes için
    /// kilitlenir.
    /// </summary>
    public int AccountMaxAttempts { get; set; } = 20;

    public LoginThrottleLimits ToLimits() => new(MaxAttemptsPerIp, Window, LockoutDuration);
}

/// <summary>
/// Hatalı girişleri hesap + IP çifti başına sayar. Böylece bir saldırgan yanlış şifre deneyerek yalnızca KENDİ IP'sini
/// engeller; hesabın sahibi başka bir IP'den girmeye devam eder. Hesap genelindeki kilit
/// (<c>User.RegisterFailedAccess</c>) daha yüksek bir sınırla ikinci savunma hattıdır.
/// </summary>
/// <remarks>
/// Hesap anahtarı olarak e-postayı (normalize) ver; kayıtlı olmayan e-postalar için de çağır, yoksa yanıttan
/// e-postanın kayıtlı olup olmadığı anlaşılır. IP, <c>HttpContext.Connection.RemoteIpAddress</c>'tir: uygulama bir
/// proxy arkasındaysa <c>UseForwardedHeaders</c> doğru yapılandırılmalı, yoksa herkes proxy'nin IP'sini paylaşır.
/// Varsayılan uygulama bellektedir (tek sunucu); birden çok sunucuda <c>CanFx.Redis.ScaleOut</c> →
/// <c>UseForLoginThrottle()</c>.
/// </remarks>
/// <example>
/// <code>
/// if (await throttle.IsBlockedAsync(email, client.IpAddress, cancellationToken: ct))
///     throw new UnauthorizedException("locked_out");
///
/// if (!passwordValid)
/// {
///     await throttle.RegisterFailureAsync(email, client.IpAddress, cancellationToken: ct);
///     user?.RegisterFailedAccess(now, options.AccountMaxAttempts);
///     ...
/// }
///
/// await throttle.ResetAsync(email, client.IpAddress, ct);   // başarılı giriş
/// </code>
/// </example>
public interface ILoginThrottle
{
    /// <summary>Bu IP, bu hesap için şu an engelli mi.</summary>
    Task<bool> IsBlockedAsync(string account, string? ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Hatalı denemeyi kaydeder; sınıra ulaşıldıysa (artık engelliyse) <see langword="true"/>.</summary>
    /// <param name="account">Hesap anahtarı (e-posta).</param>
    /// <param name="ipAddress">İstemcinin IP'si; bilinmiyorsa tüm bilinmeyenler tek kovada sayılır.</param>
    /// <param name="limits">Bu çağrı için sınırlar (ör. ayarlardan); verilmezse <see cref="LoginThrottleOptions"/>.</param>
    /// <param name="cancellationToken">İptal.</param>
    Task<bool> RegisterFailureAsync(string account, string? ipAddress, LoginThrottleLimits? limits = null, CancellationToken cancellationToken = default);

    /// <summary>Başarılı girişte sayacı ve engeli siler.</summary>
    Task ResetAsync(string account, string? ipAddress, CancellationToken cancellationToken = default);
}

/// <summary>Hesap + IP çiftinin depolama anahtarı (e-posta açık metin olarak saklanmaz).</summary>
public static class LoginThrottleKey
{
    public static string Create(string account, string? ipAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        string ip = string.IsNullOrWhiteSpace(ipAddress) ? "unknown" : ipAddress.Trim();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(account.Trim().ToUpperInvariant() + "\n" + ip));
        return Convert.ToHexString(hash, 0, 16);
    }
}

/// <summary>Bellek içi sayaç (tek sunucu; uygulama yeniden başlayınca sıfırlanır).</summary>
public sealed class InMemoryLoginThrottle : ILoginThrottle
{
    private const int SweepThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LoginThrottleOptions _options;
    private readonly TimeProvider _timeProvider;

    public InMemoryLoginThrottle(LoginThrottleOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.ToLimits().Validate();
        _options = options;
        _timeProvider = timeProvider;
    }

    public Task<bool> IsBlockedAsync(string account, string? ipAddress, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsBlocked(LoginThrottleKey.Create(account, ipAddress)));

    public Task<bool> RegisterFailureAsync(string account, string? ipAddress, LoginThrottleLimits? limits = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(RegisterFailure(LoginThrottleKey.Create(account, ipAddress), limits ?? _options.ToLimits()));

    public Task ResetAsync(string account, string? ipAddress, CancellationToken cancellationToken = default)
    {
        Reset(LoginThrottleKey.Create(account, ipAddress));
        return Task.CompletedTask;
    }

    /// <summary>Anahtar zaten <see cref="LoginThrottleKey"/> ile üretilmişse (Redis yedeği).</summary>
    public bool IsBlocked(string key)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        return _entries.TryGetValue(key, out Entry? entry) && entry.IsBlocked(now);
    }

    public bool RegisterFailure(string key, LoginThrottleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (_entries.Count > SweepThreshold)
            Sweep(now);

        Entry entry = _entries.GetOrAdd(key, static _ => new Entry());
        return entry.RegisterFailure(now, limits);
    }

    public void Reset(string key) => _entries.TryRemove(key, out _);

    private void Sweep(DateTimeOffset now)
    {
        foreach ((string key, Entry entry) in _entries)
        {
            if (entry.IsExpired(now))
                _entries.TryRemove(key, out _);
        }
    }

    private sealed class Entry
    {
        private readonly Lock _gate = new();
        private int _count;
        private DateTimeOffset _countExpiresAt;
        private DateTimeOffset _blockedUntil;

        public bool IsBlocked(DateTimeOffset now)
        {
            lock (_gate)
                return _blockedUntil > now;
        }

        public bool IsExpired(DateTimeOffset now)
        {
            lock (_gate)
                return _blockedUntil <= now && _countExpiresAt <= now;
        }

        public bool RegisterFailure(DateTimeOffset now, LoginThrottleLimits limits)
        {
            lock (_gate)
            {
                if (_blockedUntil > now)
                    return true;

                if (_countExpiresAt <= now)
                    _count = 0;

                _count++;
                _countExpiresAt = now.Add(limits.Window);

                if (_count < limits.MaxAttempts)
                    return false;

                _count = 0;
                _blockedUntil = now.Add(limits.LockoutDuration);
                return true;
            }
        }
    }
}
