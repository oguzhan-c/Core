using CanFx.Application.Authorization;
using CanFx.Application.Exceptions;

namespace CanFx.Application.Tests;

public class PermissionSetTests
{
    [Theory]
    [InlineData("*", "colors.read", true)]
    [InlineData("*", "*", true)]
    [InlineData("colors.*", "colors.read", true)]
    [InlineData("colors.*", "colors.*", true)]
    [InlineData("app.*", "app.colors.*", true)]
    [InlineData("colors.read", "colors.read", true)]
    [InlineData("COLORS.READ", "colors.read", true)]
    [InlineData("colors.read", "colors.*", false)]
    [InlineData("colors.*", "*", false)]
    [InlineData("colors.*", "colorsx.read", false)]
    [InlineData("app.colors.*", "app.*", false)]
    [InlineData("colors.write", "colors.read", false)]
    public void CoversPattern(string granted, string required, bool expected) =>
        Assert.Equal(expected, PermissionSet.CoversPattern(granted, required));

    [Fact]
    public void Missing_lists_uncovered_permissions()
    {
        IReadOnlyList<string> missing = PermissionSet.Missing(["colors.read", "colors.write", "brands.read"], ["colors.*"]);
        Assert.Equal(new[] { "brands.read" }, missing);
        Assert.True(PermissionSet.IsSubset(["colors.read"], ["colors.read", "colors.write"]));
        Assert.False(PermissionSet.IsSubset(["colors.*"], ["colors.read", "colors.write"]));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("colors.read", true)]
    [InlineData("colors.sync-color", true)]
    [InlineData("identity.users.assign-role", true)]
    [InlineData("colors.*", true)]
    [InlineData("Colors.read", false)]
    [InlineData("colors..read", false)]
    [InlineData("colors.*.read", false)]
    [InlineData("*.read", false)]
    [InlineData("", false)]
    public void IsValidName(string name, bool expected) => Assert.Equal(expected, PermissionSet.IsValidName(name));
}

public class AccessGrantGuardTests
{
    private const string ActorId = "11111111-1111-1111-1111-111111111111";
    private const string OtherId = "22222222-2222-2222-2222-222222222222";

    private static AccessGrantGuard Guard(string[] roles, string[] permissions, string? id = ActorId) =>
        new(new FakeUser(id, roles, permissions), new CanApplicationOptions { AdminRole = "Admin" });

    [Fact]
    public void Admin_can_grant_anything()
    {
        AccessGrantGuard guard = Guard(["Admin"], []);
        guard.EnsureCanGrant(["colors.delete", "identity.roles.grant"]);
        guard.EnsureCanAssignRole(OtherId, "Admin", []);
        Assert.Equal(new[] { "*" }, guard.ActorPermissions);
    }

    [Fact]
    public void Cannot_grant_permissions_actor_does_not_have()
    {
        AccessGrantGuard guard = Guard(["Moderator"], ["identity.roles.grant", "colors.read"]);
        guard.EnsureCanGrant(["colors.read"]);

        var ex = Assert.Throws<ForbiddenException>(() => guard.EnsureCanGrant(["colors.read", "colors.delete"]));
        Assert.Equal(AccessGrantErrorCodes.ExceedsOwn, ex.Code);
        Assert.Contains("colors.delete", ex.Message);
    }

    [Fact]
    public void Wildcard_actor_can_grant_concrete_but_not_wider()
    {
        AccessGrantGuard guard = Guard(["Moderator"], ["colors.*"]);
        guard.EnsureCanGrant(["colors.read", "colors.*"]);
        Assert.Throws<ForbiddenException>(() => guard.EnsureCanGrant(["*"]));
    }

    [Fact]
    public void Role_assignment_requires_all_role_permissions_and_only_admin_assigns_admin()
    {
        AccessGrantGuard guard = Guard(["Moderator"], ["identity.users.assign-role", "colors.read"]);
        guard.EnsureCanAssignRole(OtherId, "Viewer", ["colors.read"]);

        Assert.Throws<ForbiddenException>(() => guard.EnsureCanAssignRole(OtherId, "Editor", ["colors.read", "colors.write"]));
        Assert.Throws<ForbiddenException>(() => guard.EnsureCanAssignRole(OtherId, "Admin", []));
    }

    [Fact]
    public void Nobody_changes_own_roles_or_permissions()
    {
        foreach (AccessGrantGuard guard in new[] { Guard(["Admin"], []), Guard(["Moderator"], ["*"]) })
        {
            var assign = Assert.Throws<ForbiddenException>(() => guard.EnsureCanAssignRole(ActorId, "Viewer", []));
            Assert.Equal(AccessGrantErrorCodes.SelfModification, assign.Code);
            Assert.Throws<ForbiddenException>(() => guard.EnsureCanChangeUserPermissions(ActorId, ["colors.read"]));
        }
    }

    [Fact]
    public void Non_admin_cannot_edit_own_role()
    {
        AccessGrantGuard guard = Guard(["Moderator"], ["identity.roles.grant", "colors.*"]);
        var ex = Assert.Throws<ForbiddenException>(() => guard.EnsureCanManageRole("Moderator", isSystemRole: false, RoleChange.Permissions));
        Assert.Equal(AccessGrantErrorCodes.SelfModification, ex.Code);
        guard.EnsureCanManageRole("Viewer", isSystemRole: false, RoleChange.Permissions);
    }

    [Theory]
    [InlineData(RoleChange.Rename)]
    [InlineData(RoleChange.Delete)]
    public void System_roles_cannot_be_renamed_or_deleted(RoleChange change)
    {
        var ex = Assert.Throws<ConflictException>(() => Guard(["Admin"], []).EnsureCanManageRole("User", isSystemRole: true, change));
        Assert.Equal(AccessGrantErrorCodes.SystemRole, ex.Code);
    }

    [Fact]
    public void System_role_details_and_permissions_are_editable_except_admin_permissions()
    {
        AccessGrantGuard guard = Guard(["Admin"], []);
        guard.EnsureCanManageRole("User", isSystemRole: true, RoleChange.Details);
        guard.EnsureCanManageRole("User", isSystemRole: true, RoleChange.Permissions);
        Assert.Throws<ConflictException>(() => guard.EnsureCanManageRole("Admin", isSystemRole: true, RoleChange.Permissions));
    }

    [Fact]
    public void Last_admin_cannot_be_removed()
    {
        AccessGrantGuard guard = Guard(["Admin"], []);
        guard.EnsureAdminRemains(1);
        var ex = Assert.Throws<ConflictException>(() => guard.EnsureAdminRemains(0));
        Assert.Equal(AccessGrantErrorCodes.LastAdmin, ex.Code);
    }

    [Fact]
    public void Anonymous_actor_is_rejected()
    {
        Assert.Throws<UnauthorizedException>(() => Guard([], [], id: null).EnsureCanGrant(["colors.read"]));
    }

    private sealed class FakeUser(string? id, string[] roles, string[] permissions) : ICurrentUser
    {
        public string? Id => id;

        public string? UserName => null;

        public string? Email => null;

        public IReadOnlyCollection<string> Roles => roles;

        public IReadOnlyCollection<string> Permissions => permissions;
    }
}
