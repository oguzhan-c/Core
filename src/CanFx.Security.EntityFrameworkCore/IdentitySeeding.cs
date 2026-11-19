using CanFx.Security.Entities;
using CanFx.Security.Hashing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CanFx.Security.EntityFrameworkCore;

/// <summary>Kimlik başlangıç verisi: yetki kataloğu, sistem rolleri, başlangıç yetkileri, yönetici hesabı.</summary>
public sealed class IdentitySeedOptions<TUser, TId>
    where TUser : User<TId>
    where TId : notnull, IEquatable<TId>
{
    /// <summary>Yetki kataloğu (koddan). Tablo buna eşitlenir: eksik eklenir, koddan çıkan kullanımdan kalkar.</summary>
    public IEnumerable<(string Name, string Description)> Permissions { get; set; } = [];

    /// <summary>Sistem rolleri (silinemez işaretlenir).</summary>
    public IEnumerable<string> Roles { get; set; } = [];

    /// <summary>
    /// Rol → başlangıç yetkileri. Yalnızca rol YENİ oluşturulduğunda ya da yetki kataloğa YENİ eklendiğinde verilir:
    /// yönetim ekranından geri alınan yetki yeniden başlatınca geri gelmez.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> RoleGrants { get; set; } = new Dictionary<string, string[]>(StringComparer.Ordinal);

    /// <summary>Yeni kayıt anahtarı. Guid: <c>Guid.CreateVersion7</c>; int / long (veritabanı üretir): <c>default</c>.</summary>
    public Func<TId> NewId { get; set; } = () => default!;

    /// <summary>Anahtarı veritabanı üretiyorsa (int / long) <see langword="true"/>: bağlı kayıtlardan önce kaydedilir.</summary>
    public bool DatabaseGeneratedIds { get; set; }

    /// <summary>Yönetici e-postası. Şifre boşsa yönetici oluşturulmaz (uyarı loglanır).</summary>
    public string? AdminEmail { get; set; }

    /// <summary>(gizli) Yönetici şifresi: user-secrets ya da appsettings (<c>Seed:AdminPassword</c>).</summary>
    public string? AdminPassword { get; set; }

    /// <summary>Yöneticiye verilecek rol.</summary>
    public string AdminRole { get; set; } = "Admin";

    /// <summary>Yönetici kullanıcısını kurar: (e-posta, şifre özeti) → kullanıcı. Verilmezse yönetici oluşturulmaz.</summary>
    public Func<string, string, TUser>? CreateAdmin { get; set; }

    /// <summary>
    /// Seed'in bu bağlamda çalışıp çalışmayacağı (ör. tenant veritabanlarında çalışmasın:
    /// <c>sp => sp.GetService&lt;TenantContext&gt;()?.Tenant is null</c>). Boşsa her zaman çalışır.
    /// </summary>
    public Func<IServiceProvider, bool>? RunWhen { get; set; }
}

/// <summary>
/// Kimlik başlangıç verisini yazar. İdempotent: her çalışmada eksikleri ekler, var olana dokunmaz (yönetici şifresi
/// sonradan değiştirilmiş olabilir). EF Core'un seed kancasından (<see cref="IdentitySeedingExtensions.UseCanIdentitySeeding{TUser, TId}"/>)
/// ya da elle (<see cref="IdentitySeedingExtensions.SeedIdentityAsync{TContext, TUser, TId}"/>) çalışır.
/// </summary>
public sealed class IdentitySeeder<TUser, TId>
    where TUser : User<TId>
    where TId : notnull, IEquatable<TId>
{
    private static readonly Action<ILogger, string, Exception?> AdminPasswordMissing = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(1, "AdminPasswordMissing"),
        "Seed: Seed:AdminPassword verilmediği için {Email} yönetici hesabı oluşturulmadı. dotnet user-secrets set \"Seed:AdminPassword\" \"...\" ile (ya da appsettings.json → Seed:AdminPassword) ekleyip uygulamayı yeniden başlat.");

    private readonly IdentitySeedOptions<TUser, TId> _options;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ILogger _logger;

    public IdentitySeeder(IdentitySeedOptions<TUser, TId> options, IPasswordHasher passwordHasher, ILogger<IdentitySeeder<TUser, TId>>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        _options = options;
        _passwordHasher = passwordHasher;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public IdentitySeedOptions<TUser, TId> Options => _options;

    public async Task SeedAsync(DbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        Dictionary<string, OperationClaim<TId>> claims =
            await db.SyncOperationClaimsAsync(_options.Permissions, _options.NewId, cancellationToken).ConfigureAwait(false);
        HashSet<string> newClaims = claims.Where(c => db.Entry(c.Value).State == EntityState.Added).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        if (_options.DatabaseGeneratedIds)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);   // rollere bağlamadan önce Id'ler belli olsun

        Dictionary<string, Role<TId>> roles = await SeedRolesAsync(db, claims, newClaims, cancellationToken).ConfigureAwait(false);
        if (_options.DatabaseGeneratedIds)
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await SeedAdminAsync(db, roles, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, Role<TId>>> SeedRolesAsync(
        DbContext db, Dictionary<string, OperationClaim<TId>> claims, HashSet<string> newClaims, CancellationToken cancellationToken)
    {
        Dictionary<string, Role<TId>> existing = await db.Set<Role<TId>>().Include(r => r.OperationClaims)
            .ToDictionaryAsync(r => r.Name, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

        foreach (string name in _options.Roles.Distinct(StringComparer.Ordinal))
        {
            bool created = false;
            if (!existing.TryGetValue(name, out Role<TId>? role))
            {
                role = new Role<TId>(_options.NewId(), name);
                db.Set<Role<TId>>().Add(role);
                existing[name] = role;
                created = true;
            }

            if (!role.IsSystem)
                role.MarkAsSystem();

            if (!_options.RoleGrants.TryGetValue(name, out string[]? grants))
                continue;

            foreach (string grant in grants)
            {
                if (!claims.TryGetValue(grant.Trim().ToLowerInvariant(), out OperationClaim<TId>? claim))
                    throw new InvalidOperationException($"'{name}' rolüne verilen '{grant}' yetkisi katalogda yok (IdentitySeedOptions.Permissions).");
                if (created || newClaims.Contains(claim.Name))
                    role.GrantOperationClaim(claim);
            }
        }

        return existing;
    }

    private async Task SeedAdminAsync(DbContext db, Dictionary<string, Role<TId>> roles, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminEmail) || _options.CreateAdmin is null)
            return;

        if (string.IsNullOrWhiteSpace(_options.AdminPassword))
        {
            AdminPasswordMissing(_logger, _options.AdminEmail, null);
            return;
        }

        string normalized = _options.AdminEmail.Trim().ToUpperInvariant();
        bool exists = await db.Set<TUser>().IgnoreQueryFilters().AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken).ConfigureAwait(false)
            || db.Set<TUser>().Local.Any(u => u.NormalizedEmail == normalized);
        if (exists)
            return;   // var olana dokunma: şifresi değiştirilmiş olabilir

        TUser user = _options.CreateAdmin(_options.AdminEmail.Trim(), _passwordHasher.Hash(_options.AdminPassword));
        if (roles.TryGetValue(_options.AdminRole, out Role<TId>? adminRole) && user.UserRoles.All(r => !r.RoleId.Equals(adminRole.Id)))
            user.UserRoles.Add(new UserRole<TId>(user.Id, adminRole.Id));
        db.Set<TUser>().Add(user);
    }
}

public static class IdentitySeedingExtensions
{
    /// <summary>Kimlik başlangıç verisini kaydeder. Çalıştırmak için: <see cref="UseCanIdentitySeeding{TUser, TId}"/> ya da <see cref="SeedIdentityAsync{TContext, TUser, TId}"/>.</summary>
    /// <example>
    /// <code>
    /// services.AddCanIdentitySeed&lt;AppUser, Guid&gt;(o =>
    /// {
    ///     o.Permissions = AppOperationClaims.All;
    ///     o.Roles = Roles.All;
    ///     o.RoleGrants = AppOperationClaims.RoleGrants;
    ///     o.NewId = Guid.CreateVersion7;
    ///     o.AdminEmail = "admin@example.com";
    ///     o.AdminPassword = configuration["Seed:AdminPassword"];
    ///     o.CreateAdmin = (email, hash) => AppUser.Create(email, hash);
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanIdentitySeed<TUser, TId>(this IServiceCollection services, Action<IdentitySeedOptions<TUser, TId>> configure)
        where TUser : User<TId>
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new IdentitySeedOptions<TUser, TId>();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddTransient<IdentitySeeder<TUser, TId>>();
        return services;
    }

    /// <summary>
    /// Seed'i EF Core'un seed kancasına bağlar (<c>UseAsyncSeeding</c> / <c>UseSeeding</c>): <c>dotnet ef database update</c>,
    /// migration bundle'ları, <c>Migrate()</c> ve <c>EnsureCreated()</c> aynı kodu migration kilidi altında çalıştırır.
    /// <paramref name="services"/>: DbContext kaydındaki servis sağlayıcı (<c>AddDbContext((sp, o) => ...)</c>).
    /// </summary>
    /// <remarks>Bir DbContext'te tek seed kancası olur; başka seed de gerekiyorsa onu aynı kancadan çağır.</remarks>
    public static DbContextOptionsBuilder UseCanIdentitySeeding<TUser, TId>(this DbContextOptionsBuilder options, IServiceProvider services)
        where TUser : User<TId>
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        return options
            .UseAsyncSeeding((context, _, cancellationToken) => RunAsync<TUser, TId>(services, context, cancellationToken))
            .UseSeeding((context, _) => RunAsync<TUser, TId>(services, context, CancellationToken.None).GetAwaiter().GetResult());
    }

    /// <summary>Seed'i elle çalıştırır (ör. <c>StartupMode = None</c> iken ya da testlerde).</summary>
    public static async Task SeedIdentityAsync<TContext, TUser, TId>(this IServiceProvider services, CancellationToken cancellationToken = default)
        where TContext : DbContext
        where TUser : User<TId>
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        TContext db = scope.ServiceProvider.GetRequiredService<TContext>();
        await RunAsync<TUser, TId>(scope.ServiceProvider, db, cancellationToken).ConfigureAwait(false);
    }

    private static Task RunAsync<TUser, TId>(IServiceProvider services, DbContext context, CancellationToken cancellationToken)
        where TUser : User<TId>
        where TId : notnull, IEquatable<TId>
    {
        IdentitySeeder<TUser, TId> seeder = services.GetService<IdentitySeeder<TUser, TId>>()
            ?? throw new InvalidOperationException($"Kimlik seed'i kayıtlı değil: services.AddCanIdentitySeed<{typeof(TUser).Name}, {typeof(TId).Name}>(...).");
        return seeder.Options.RunWhen?.Invoke(services) == false
            ? Task.CompletedTask
            : seeder.SeedAsync(context, cancellationToken);
    }
}
