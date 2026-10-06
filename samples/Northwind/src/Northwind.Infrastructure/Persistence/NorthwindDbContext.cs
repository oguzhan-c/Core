using Can.Core.Application;
using Can.Core.Persistence.Context;
using Can.Core.Security.Entities;
using Can.Core.Security.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Northwind.Domain.Catalog;
using Northwind.Domain.Customers;
using Northwind.Domain.Employees;
using Northwind.Domain.Identity;
using Northwind.Domain.Orders;
using Northwind.Domain.Shipping;

namespace Northwind.Infrastructure.Persistence;

/// <summary>
/// Uygulamanın DbContext'i. <see cref="CanDbContext"/> sayesinde soft delete ve tenant filtreleri tüm
/// <c>TenantAggregateRoot</c>'lara otomatik uygulanır.
/// </summary>
public sealed class NorthwindDbContext : CanDbContext
{
    public NorthwindDbContext(DbContextOptions<NorthwindDbContext> options, ICurrentTenant currentTenant)
        : base(options, currentTenant) { }

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Territory> Territories => Set<Territory>();
    public DbSet<Shipper> Shippers => Set<Shipper>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role<Guid>> Roles => Set<Role<Guid>>();
    public DbSet<OperationClaim<Guid>> OperationClaims => Set<OperationClaim<Guid>>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyCanSecurityModel<AppUser, Guid>(o => o.Schema = "identity");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NorthwindDbContext).Assembly);
        modelBuilder.AddCanOutbox(schema: "infra");
        modelBuilder.AddCanAuditTrail(schema: "infra");
    }
}
