using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using CanFx.Security.Entities;
using CanFx.Security.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CanFx.Security.EntityFrameworkCore;

/// <summary>Güvenlik damgası denetiminin ayarları.</summary>
public sealed class SecurityStampValidatorOptions
{
    /// <summary>
    /// Damganın bellekte tutulma süresi (her istekte veritabanına gitmemek için). Damga değişikliği aynı sunucuda
    /// hemen (<see cref="ISecurityStampValidator.Invalidate"/>), diğer sunucularda en geç bu süre sonra yansır.
    /// <see cref="TimeSpan.Zero"/>: önbellek yok.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Damgayı <typeparamref name="TContext"/>'teki kullanıcı kaydından okur. Sorgu isteğin scope'unda çalışır: tenant
/// filtresi ve soft delete uygulanır (silinmiş kullanıcının token'ı reddedilir); tenant başına veritabanında da doğru
/// veritabanına gider (WebApi'de tenant çözümünden sonra çağrılır).
/// </summary>
public sealed class EfSecurityStampValidator<TContext, TUser, TId> : ISecurityStampValidator
    where TContext : DbContext
    where TUser : User<TId>
    where TId : notnull, IEquatable<TId>
{
    private static readonly TypeConverter IdConverter = TypeDescriptor.GetConverter(typeof(TId));

    private readonly TContext _context;
    private readonly IMemoryCache _cache;
    private readonly SecurityStampValidatorOptions _options;

    public EfSecurityStampValidator(TContext context, IMemoryCache cache, SecurityStampValidatorOptions options)
    {
        _context = context;
        _cache = cache;
        _options = options;
    }

    public async ValueTask<bool> IsCurrentAsync(string userId, string securityStamp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        string? current = await CurrentAsync(userId, cancellationToken).ConfigureAwait(false);
        return current is not null && string.Equals(current, securityStamp, StringComparison.Ordinal);
    }

    public void Invalidate(string userId) => _cache.Remove(CacheKey(userId));

    private async ValueTask<string?> CurrentAsync(string userId, CancellationToken cancellationToken)
    {
        string key = CacheKey(userId);
        if (_options.CacheDuration > TimeSpan.Zero && _cache.TryGetValue(key, out string? cached))
            return cached;

        if (!TryParse(userId, out TId? id))
            return null;

        ParameterExpression user = Expression.Parameter(typeof(TUser), "u");
        Expression<Func<TUser, bool>> byId = Expression.Lambda<Func<TUser, bool>>(
            Expression.Equal(Expression.Property(user, nameof(User<TId>.Id)), Expression.Constant(id, typeof(TId))),
            user);

        string? stamp = await _context.Set<TUser>().AsNoTracking().Where(byId).Select(u => u.SecurityStamp)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (_options.CacheDuration > TimeSpan.Zero && stamp is not null)
            _cache.Set(key, stamp, _options.CacheDuration);
        return stamp;
    }

    private static bool TryParse(string value, out TId? id)
    {
        try
        {
            id = (TId?)IdConverter.ConvertFromString(null, CultureInfo.InvariantCulture, value);
            return id is not null;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException)
        {
            id = default;
            return false;
        }
    }

    private static string CacheKey(string userId) => "can:sstamp:" + typeof(TUser).FullName + ":" + userId;
}

public static class SecurityStampValidatorServiceCollectionExtensions
{
    /// <summary>
    /// Güvenlik damgası denetimi için EF Core uygulaması. WebApi'de <c>UseCanSecurityStampValidation()</c> ile birlikte.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanSecurityStampValidator&lt;AppDbContext, AppUser, Guid&gt;();
    /// app.UseAuthentication(); app.UseCanTenantResolution(); app.UseCanSecurityStampValidation(); app.UseAuthorization();
    /// </code>
    /// </example>
    public static IServiceCollection AddCanSecurityStampValidator<TContext, TUser, TId>(this IServiceCollection services, Action<SecurityStampValidatorOptions>? configure = null)
        where TContext : DbContext
        where TUser : User<TId>
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new SecurityStampValidatorOptions();
        configure?.Invoke(options);
        services.AddMemoryCache();
        services.RemoveAll<SecurityStampValidatorOptions>();
        services.AddSingleton(options);
        services.RemoveAll<ISecurityStampValidator>();
        services.AddScoped<ISecurityStampValidator, EfSecurityStampValidator<TContext, TUser, TId>>();
        return services;
    }
}
