using Can.Core.Persistence.Seeding;
using Can.Core.Security.Entities;
using Can.Core.Security.Hashing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CanAppTemplate.Application.Common;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Persistence.Seeding;

/// <summary>Roller, yetkiler ve yönetici hesabı. İdempotent: her açılışta eksikleri ekler, var olana dokunmaz.</summary>
internal sealed partial class IdentitySeeder : IDataSeeder
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SeedOptions _options;
    private readonly ILogger<IdentitySeeder> _logger;

    public IdentitySeeder(AppDbContext db, IPasswordHasher passwordHasher, SeedOptions options, ILogger<IdentitySeeder> logger)
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

        Dictionary<string, OperationClaim<Guid>> claims = await SeedPermissionsAsync(cancellationToken);
        Dictionary<string, Role<Guid>> roles = await SeedRolesAsync(claims, cancellationToken);

        if (string.IsNullOrWhiteSpace(_options.AdminPassword))
            LogAdminPasswordMissing(_options.AdminEmail);
        else
            await EnsureUserAsync(_options.AdminEmail, "Platform", "Yöneticisi", _options.AdminPassword, roles[Roles.Admin], cancellationToken);

        // TODO(can-gen): Geliştirme için demo kullanıcılar / başlangıç verisi gerekiyorsa burada ya da ayrı bir IDataSeeder'da ekle.

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<string, OperationClaim<Guid>>> SeedPermissionsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, OperationClaim<Guid>> existing = await _db.OperationClaims.ToDictionaryAsync(c => c.Name, cancellationToken);
        foreach ((string name, string description) in AppPermissions.All)
        {
            if (existing.ContainsKey(name))
                continue;

            var claim = new OperationClaim<Guid>(Guid.CreateVersion7(), name, description);
            _db.OperationClaims.Add(claim);
            existing[name] = claim;
        }

        return existing;
    }

    private async Task<Dictionary<string, Role<Guid>>> SeedRolesAsync(Dictionary<string, OperationClaim<Guid>> claims, CancellationToken cancellationToken)
    {
        Dictionary<string, Role<Guid>> existing = await _db.Roles.Include(r => r.OperationClaims).ToDictionaryAsync(r => r.Name, cancellationToken);
        foreach (string name in Roles.All)
        {
            if (!existing.TryGetValue(name, out Role<Guid>? role))
            {
                role = new Role<Guid>(Guid.CreateVersion7(), name);
                _db.Roles.Add(role);
                existing[name] = role;
            }

            if (AppPermissions.RoleGrants.TryGetValue(name, out string[]? grants))
            {
                foreach (string grant in grants)
                    role.GrantOperationClaim(claims[grant]);
            }
        }

        return existing;
    }

    /// <summary>Kullanıcı yoksa oluşturur (varsa dokunmaz: şifresi değiştirilmiş olabilir).</summary>
    internal async Task<AppUser> EnsureUserAsync(string email, string firstName, string lastName, string password, Role<Guid> role, CancellationToken cancellationToken)
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
