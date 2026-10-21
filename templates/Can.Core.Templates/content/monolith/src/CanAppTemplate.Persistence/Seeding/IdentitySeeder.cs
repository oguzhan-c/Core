using Can.Core.Persistence.Seeding;
using Can.Core.Security.EntityFrameworkCore;
using Can.Core.Security.Entities;
using Can.Core.Security.Hashing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CanAppTemplate.Application.Services.OperationClaims;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;
using CanAppTemplate.Persistence.Contexts;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Persistence.Seeding;

/// <summary>Roller, yetkiler ve yönetici hesabı. İdempotent: her açılışta eksikleri ekler, var olana dokunmaz.</summary>
internal sealed partial class IdentitySeeder : IDataSeeder
{
    private readonly BaseDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SeedOptions _options;
    private readonly ILogger<IdentitySeeder> _logger;

    public IdentitySeeder(BaseDbContext db, IPasswordHasher passwordHasher, SeedOptions options, ILogger<IdentitySeeder> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _options = options;
        _logger = logger;
    }

    public int Order => 1;

    public async Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken)
    {
        if (!context.IsHost)
            return;

        Dictionary<string, OperationClaim<IdentityKey>> claims = await SeedPermissionsAsync(cancellationToken);
        HashSet<string> newClaims = claims.Where(c => _db.Entry(c.Value).State == EntityState.Added).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        // int / long anahtarda yeni yetkilerin Id'si kaydedince belli olur; rollere bağlamadan önce kaydet.
        if (IdentityKeys.IsDatabaseGenerated)
            await _db.SaveChangesAsync(cancellationToken);

        Dictionary<string, Role<IdentityKey>> roles = await SeedRolesAsync(claims, newClaims, cancellationToken);
        if (IdentityKeys.IsDatabaseGenerated)
            await _db.SaveChangesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(_options.AdminPassword))
            LogAdminPasswordMissing(_options.AdminEmail);
        else
            await EnsureUserAsync(_options.AdminEmail, "Platform", "Yöneticisi", _options.AdminPassword, roles[Roles.Admin], cancellationToken);

        // TODO(can-gen): Geliştirme için demo kullanıcılar / başlangıç verisi gerekiyorsa burada ya da ayrı bir IDataSeeder'da ekle.

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Yetki tablosunu koddaki kataloğa eşitler (eksik → eklenir, koddan çıkan → kullanımdan kalkar).</summary>
    private Task<Dictionary<string, OperationClaim<IdentityKey>>> SeedPermissionsAsync(CancellationToken cancellationToken) =>
        _db.SyncOperationClaimsAsync<IdentityKey>(AppOperationClaims.All, () => IdentityKeys.New(), cancellationToken);

    /// <summary>
    /// Sistem rollerini oluşturur ve başlangıç yetkilerini verir. Başlangıç yetkisi yalnızca rol YENİ oluşturulduysa ya da
    /// yetki kataloğa YENİ eklendiyse verilir: yönetim uçlarından geri alınan yetki yeniden başlatınca geri gelmez.
    /// </summary>
    private async Task<Dictionary<string, Role<IdentityKey>>> SeedRolesAsync(Dictionary<string, OperationClaim<IdentityKey>> claims, HashSet<string> newClaims, CancellationToken cancellationToken)
    {
        Dictionary<string, Role<IdentityKey>> existing = await _db.Roles.Include(r => r.OperationClaims).ToDictionaryAsync(r => r.Name, cancellationToken);
        foreach (string name in Roles.All)
        {
            bool created = false;
            if (!existing.TryGetValue(name, out Role<IdentityKey>? role))
            {
                role = new Role<IdentityKey>(IdentityKeys.New(), name);
                _db.Roles.Add(role);
                existing[name] = role;
                created = true;
            }

            if (!role.IsSystem)
                role.MarkAsSystem();

            if (AppOperationClaims.RoleGrants.TryGetValue(name, out string[]? grants))
            {
                foreach (string grant in grants)
                {
                    OperationClaim<IdentityKey> claim = claims[grant];
                    if (created || newClaims.Contains(claim.Name))
                        role.GrantOperationClaim(claim);
                }
            }
        }

        return existing;
    }

    /// <summary>Kullanıcı yoksa oluşturur (varsa dokunmaz: şifresi değiştirilmiş olabilir).</summary>
    internal async Task<AppUser> EnsureUserAsync(string email, string firstName, string lastName, string password, Role<IdentityKey> role, CancellationToken cancellationToken)
    {
        string normalized = email.ToUpperInvariant();
        AppUser? user = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken)
            ?? _db.Users.Local.FirstOrDefault(u => u.NormalizedEmail == normalized);
        if (user is not null)
            return user;

        user = AppUser.Create(email, firstName, lastName, _passwordHasher.Hash(password)).ThrowIfFailure();
        user.AddRole(role);
        _db.Users.Add(user);
        return user;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed: Seed:AdminPassword verilmediği için {Email} yönetici hesabı oluşturulmadı. dotnet user-secrets set \"Seed:AdminPassword\" \"...\" ile ekleyip uygulamayı yeniden başlat.")]
    private partial void LogAdminPasswordMissing(string email);
}
