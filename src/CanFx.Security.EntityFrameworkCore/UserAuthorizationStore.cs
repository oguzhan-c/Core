using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using CanFx.Security.Authorization;
using CanFx.Security.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CanFx.Security.EntityFrameworkCore;

/// <summary>Sunucu taraflı rol / yetki çözümlemesinin ayarları.</summary>
public sealed class UserAuthorizationStoreOptions
{
    /// <summary>
    /// Kullanıcının rolleri ve yetkileri bellekte bu süre tutulur. Değişiklik aynı sunucuda hemen
    /// (<see cref="IUserAuthorizationStore.Invalidate"/>), diğer sunucularda en geç bu süre sonra yansır.
    /// <see cref="TimeSpan.Zero"/>: önbellek yok.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary><c>InvalidateAll</c> için önbellek kuşağı (uygulama boyunca tek).</summary>
internal sealed class UserAuthorizationCacheGeneration
{
    private long _value;

    public long Value => Interlocked.Read(ref _value);

    public void Next() => Interlocked.Increment(ref _value);
}

/// <summary>
/// Rolleri ve yetkileri <typeparamref name="TContext"/>'ten okur (kullanımdan kalkmış yetkiler hariç). Kullanıcı sorgusu
/// isteğin scope'unda çalışır: tenant filtresi ve soft delete uygulanır (silinmiş / başka tenant'taki kullanıcı → null).
/// </summary>
public sealed class EfUserAuthorizationStore<TContext, TUser, TId> : IUserAuthorizationStore
    where TContext : DbContext
    where TUser : User<TId>
    where TId : notnull, IEquatable<TId>
{
    private static readonly TypeConverter IdConverter = TypeDescriptor.GetConverter(typeof(TId));

    private readonly TContext _context;
    private readonly IMemoryCache _cache;
    private readonly UserAuthorizationStoreOptions _options;
    private readonly UserAuthorizationCacheGeneration _generation;

    internal EfUserAuthorizationStore(TContext context, IMemoryCache cache, UserAuthorizationStoreOptions options, UserAuthorizationCacheGeneration generation)
    {
        _context = context;
        _cache = cache;
        _options = options;
        _generation = generation;
    }

    public async ValueTask<UserAuthorizationData?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        string key = CacheKey(userId);
        if (_options.CacheDuration > TimeSpan.Zero && _cache.TryGetValue(key, out UserAuthorizationData? cached))
            return cached;

        if (!TryParse(userId, out TId? id) || id is null)
            return null;

        bool exists = await _context.Set<TUser>().AsNoTracking().Where(ById<TUser>(nameof(User<TId>.Id), id))
            .AnyAsync(cancellationToken).ConfigureAwait(false);
        if (!exists)
            return null;

        List<string> roles = await _context.Set<UserRole<TId>>().AsNoTracking()
            .Where(ById<UserRole<TId>>(nameof(UserRole<TId>.UserId), id))
            .Select(ur => ur.Role!.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<string> rolePermissions = await _context.Set<UserRole<TId>>().AsNoTracking()
            .Where(ById<UserRole<TId>>(nameof(UserRole<TId>.UserId), id))
            .SelectMany(ur => ur.Role!.OperationClaims)
            .Where(rc => !rc.OperationClaim!.IsDeprecated)
            .Select(rc => rc.OperationClaim!.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        List<string> userPermissions = await _context.Set<UserOperationClaim<TId>>().AsNoTracking()
            .Where(ById<UserOperationClaim<TId>>(nameof(UserOperationClaim<TId>.UserId), id))
            .Where(uc => !uc.OperationClaim!.IsDeprecated)
            .Select(uc => uc.OperationClaim!.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var data = new UserAuthorizationData(
            roles.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            rolePermissions.Concat(userPermissions).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray());

        if (_options.CacheDuration > TimeSpan.Zero)
            _cache.Set(key, data, _options.CacheDuration);
        return data;
    }

    public void Invalidate(string userId) => _cache.Remove(CacheKey(userId));

    public void InvalidateAll() => _generation.Next();

    private string CacheKey(string userId) =>
        "can:authz:" + _generation.Value.ToString(CultureInfo.InvariantCulture) + ":" + typeof(TUser).FullName + ":" + userId;

    private static Expression<Func<T, bool>> ById<T>(string property, TId id)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(T), "x");
        return Expression.Lambda<Func<T, bool>>(
            Expression.Equal(Expression.Property(parameter, property), Expression.Constant(id, typeof(TId))),
            parameter);
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
}

public static class UserAuthorizationStoreServiceCollectionExtensions
{
    /// <summary>
    /// Sunucu taraflı rol / yetki çözümlemesi (EF Core). WebApi'de <c>UseCanPermissionResolution()</c> ile birlikte:
    /// token'daki rol / yetki claim'leri yok sayılır, her istekte (önbellekli) veritabanındakiler kullanılır.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanUserAuthorizationStore&lt;AppDbContext, AppUser, Guid&gt;();
    /// app.UseAuthentication(); app.UseCanTenantResolution(); app.UseCanSecurityStampValidation();
    /// app.UseCanPermissionResolution(); app.UseAuthorization();
    /// </code>
    /// </example>
    public static IServiceCollection AddCanUserAuthorizationStore<TContext, TUser, TId>(this IServiceCollection services, Action<UserAuthorizationStoreOptions>? configure = null)
        where TContext : DbContext
        where TUser : User<TId>
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new UserAuthorizationStoreOptions();
        configure?.Invoke(options);
        services.AddMemoryCache();
        services.RemoveAll<UserAuthorizationStoreOptions>();
        services.AddSingleton(options);
        services.TryAddSingleton<UserAuthorizationCacheGeneration>();
        services.RemoveAll<IUserAuthorizationStore>();
        services.AddScoped<IUserAuthorizationStore>(sp => new EfUserAuthorizationStore<TContext, TUser, TId>(
            sp.GetRequiredService<TContext>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<UserAuthorizationStoreOptions>(),
            sp.GetRequiredService<UserAuthorizationCacheGeneration>()));
        return services;
    }
}
