using Can.Core.Domain.Results;
using Can.Core.Persistence.Seeding;
using Can.Core.Security.Entities;
using Can.Core.Security.Hashing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Northwind.Domain.Employees;
using Northwind.Domain.Identity;
using Northwind.Infrastructure.Persistence;

namespace Northwind.Infrastructure.Seeding;

/// <summary>Host: roller ile bölge ve satış alanları (tüm mağazaların ortak referans verisi).</summary>
internal sealed class ReferenceDataSeeder : IDataSeeder
{
    private readonly NorthwindDbContext _db;

    public ReferenceDataSeeder(NorthwindDbContext db) => _db = db;

    public int Order => 0;

    public async Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken)
    {
        if (!context.IsHost)
            return;

        List<string> existingRoles = await _db.Roles.Select(r => r.Name).ToListAsync(cancellationToken);
        foreach (string role in Roles.All.Except(existingRoles, StringComparer.Ordinal))
            _db.Roles.Add(new Role<Guid>(Guid.CreateVersion7(), role));

        await _db.SaveChangesAsync(cancellationToken);
        await SeedPermissionsAsync(cancellationToken);

        if (!await _db.Regions.AnyAsync(cancellationToken))
        {
            NorthwindSqlReader sql = NorthwindSqlReader.Load();

            foreach (NorthwindSqlReader.Row row in sql.Rows("region"))
                _db.Regions.Add(new Region(row.Int(0), row.RequiredText(1)));

            foreach (NorthwindSqlReader.Row row in sql.Rows("territories"))
                _db.Territories.Add(new Territory(row.RequiredText(0), row.RequiredText(1), row.Int(2)));
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Yetkileri ekler ve rollere <see cref="Permissions.RoleGrants"/>'teki yetkileri verir (eksik olanları).</summary>
    private async Task SeedPermissionsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, OperationClaim<Guid>> claims = await _db.OperationClaims.ToDictionaryAsync(c => c.Name, StringComparer.Ordinal, cancellationToken);
        foreach ((string name, string description) in Permissions.All.Where(p => !claims.ContainsKey(p.Name)))
        {
            var claim = new OperationClaim<Guid>(Guid.CreateVersion7(), name, description);
            _db.OperationClaims.Add(claim);
            claims[name] = claim;
        }

        List<Role<Guid>> roles = await _db.Roles.Include(r => r.OperationClaims).ToListAsync(cancellationToken);
        foreach (Role<Guid> role in roles)
        {
            if (Permissions.RoleGrants.TryGetValue(role.Name, out string[]? granted))
            {
                foreach (string name in granted)
                    role.GrantOperationClaim(claims[name]);
            }
        }
    }
}

/// <summary>Her mağazaya demo kullanıcıları: admin@, sales@, warehouse@&lt;mağaza&gt;.local.</summary>
internal sealed partial class DemoUserSeeder : IDataSeeder
{
    private readonly NorthwindDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SeedOptions _options;
    private readonly ILogger<DemoUserSeeder> _logger;

    public DemoUserSeeder(NorthwindDbContext db, IPasswordHasher passwordHasher, SeedOptions options, ILogger<DemoUserSeeder> logger)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _options = options;
        _logger = logger;
    }

    public int Order => 10;

    public async Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken)
    {
        if (context.Tenant is not { } tenant || await _db.Users.AnyAsync(cancellationToken))
            return;

        if (string.IsNullOrWhiteSpace(_options.DemoUserPassword))
        {
            LogNoPassword(tenant.Identifier);
            return;
        }

        Dictionary<string, Role<Guid>> roles = await _db.Roles.ToDictionaryAsync(r => r.Name, StringComparer.Ordinal, cancellationToken);
        string passwordHash = _passwordHasher.Hash(_options.DemoUserPassword);

        (string Name, string FirstName, string Role)[] users =
        [
            ("admin", "Yönetici", Roles.Admin),
            ("sales", "Satış", Roles.Sales),
            ("warehouse", "Depo", Roles.Warehouse),
        ];

        foreach ((string name, string firstName, string role) in users)
        {
            AppUser user = AppUser.Create($"{name}@{tenant.Identifier}.local", firstName, "Demo", passwordHash).ThrowIfFailure();
            user.AddRole(roles[role]);
            _db.Users.Add(user);
        }

        await _db.SaveChangesAsync(cancellationToken);
        LogCreated(tenant.Identifier);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{Tenant}' mağazası için demo kullanıcı oluşturulmadı: Seed:DemoUserPassword boş.")]
    private partial void LogNoPassword(string tenant);

    [LoggerMessage(Level = LogLevel.Information, Message = "'{Tenant}' mağazası için demo kullanıcılar oluşturuldu.")]
    private partial void LogCreated(string tenant);
}

/// <summary>
/// Northwind mağazasında siteyi denemek için bir müşteri hesabı: <c>customer@&lt;mağaza&gt;.local</c>, ALFKI müşterisine bağlı.
/// </summary>
internal sealed class DemoCustomerSeeder : IDataSeeder
{
    private const string DemoCustomerCode = "ALFKI";

    private readonly NorthwindDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SeedOptions _options;

    public DemoCustomerSeeder(NorthwindDbContext db, IPasswordHasher passwordHasher, SeedOptions options)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _options = options;
    }

    // Northwind verisinden (20) sonra.
    public int Order => 30;

    public async Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken)
    {
        if (context.Tenant is not { } tenant || string.IsNullOrWhiteSpace(_options.DemoUserPassword))
            return;

        Domain.Customers.Customer? customer = await _db.Customers.FirstOrDefaultAsync(c => c.Code == DemoCustomerCode, cancellationToken);
        if (customer is null || customer.UserId is not null)
            return;

        Role<Guid> role = await _db.Roles.FirstAsync(r => r.Name == Roles.Customer, cancellationToken);
        AppUser user = AppUser.Create($"customer@{tenant.Identifier}.local", "Maria", "Anders", _passwordHasher.Hash(_options.DemoUserPassword)).ThrowIfFailure();
        user.AddRole(role);
        _db.Users.Add(user);
        customer.LinkUser(user.Id).ThrowIfFailure();

        await _db.SaveChangesAsync(cancellationToken);
    }
}
