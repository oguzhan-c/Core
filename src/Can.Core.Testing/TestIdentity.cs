using Can.Core.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Testing;

/// <summary>Testte değiştirilebilen kullanıcı (singleton kaydedilir; test içinde <see cref="SignIn"/> ile değişir).</summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public string? Id { get; set; }

    public string? UserName { get; set; }

    public string? Email { get; set; }

    public List<string> RoleList { get; } = [];

    public List<string> PermissionList { get; } = [];

    public IReadOnlyCollection<string> Roles => RoleList;

    public IReadOnlyCollection<string> Permissions => PermissionList;

    public bool IsAuthenticated => Id is not null;

    public bool IsInRole(string role) => RoleList.Contains(role, StringComparer.OrdinalIgnoreCase);

    public bool HasPermission(string permission) => PermissionMatcher.IsGranted(PermissionList, permission);

    /// <summary>Kullanıcıyı değiştirir (rolleri ve yetkileri sıfırlar).</summary>
    public TestCurrentUser SignIn(string id, IEnumerable<string>? roles = null, IEnumerable<string>? permissions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        UserName ??= id;
        RoleList.Clear();
        RoleList.AddRange(roles ?? []);
        PermissionList.Clear();
        PermissionList.AddRange(permissions ?? []);
        return this;
    }

    /// <summary>Anonim kullanıcı.</summary>
    public TestCurrentUser SignOut()
    {
        Id = UserName = Email = null;
        RoleList.Clear();
        PermissionList.Clear();
        return this;
    }
}

/// <summary>Testte değiştirilebilen tenant.</summary>
public sealed class TestCurrentTenant : ICurrentTenant
{
    public object? Id { get; set; }

    /// <summary><paramref name="tenantId"/> tenant'ında çalıştırır, sonra öncekine döner.</summary>
    /// <example><code>using (tenant.Use(TenantB)) { ... }</code></example>
    public IDisposable Use(object? tenantId)
    {
        object? previous = Id;
        Id = tenantId;
        return new Restore(this, previous);
    }

    private sealed class Restore(TestCurrentTenant tenant, object? previous) : IDisposable
    {
        public void Dispose() => tenant.Id = previous;
    }
}

public static class TestingServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="ICurrentUser"/>, <see cref="ICurrentTenant"/> ve <see cref="TimeProvider"/>'ı test sürümleriyle değiştirir
    /// (verilmezse yenileri oluşturulur; servislerden <c>GetRequiredService&lt;TestCurrentUser&gt;()</c> ile alınır).
    /// </summary>
    public static IServiceCollection AddCanTestContext(this IServiceCollection services, TestCurrentUser? user = null, TestCurrentTenant? tenant = null, TestClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        user ??= new TestCurrentUser();
        tenant ??= new TestCurrentTenant();
        clock ??= new TestClock();

        services.RemoveAll<ICurrentUser>();
        services.RemoveAll<ICurrentTenant>();
        services.RemoveAll<TimeProvider>();
        services.AddSingleton(user);
        services.AddSingleton<ICurrentUser>(user);
        services.AddSingleton(tenant);
        services.AddSingleton<ICurrentTenant>(tenant);
        services.AddSingleton(clock);
        services.AddSingleton<TimeProvider>(clock);
        return services;
    }
}
