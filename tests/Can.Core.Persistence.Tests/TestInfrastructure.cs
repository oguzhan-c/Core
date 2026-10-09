using System.Collections.Concurrent;
using Can.Core.Application;
using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;
using Can.Core.Domain.Events;
using Can.Core.Domain.MultiTenancy;
using Can.Core.Mediator;
using Can.Core.Mediator.DependencyInjection;
using Can.Core.Persistence.Context;
using Can.Core.Persistence.AuditTrail;
using Can.Core.Persistence.DependencyInjection;
using Can.Core.Persistence.Outbox;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Tests;

// ------------------------------------------------------------ Domain

public sealed record ProductCreated(string Name) : DomainEvent;

public sealed record ProductPriceChanged(int ProductId, int Price) : DomainEvent;

/// <summary>Outbox üzerinden (kalıcı) yayınlanan event.</summary>
public sealed record ProductDiscontinued(int ProductId, string Name) : DomainEvent, IIntegrationEvent;

/// <summary>Değişiklik geçmişi tutulan entity.</summary>
[Audited]
public sealed class Customer : FullAuditedEntity<int>
{
    private Customer() { }

    public Customer(string name, string secret)
    {
        Name = name;
        Secret = secret;
    }

    public string Name { get; set; } = "";

    [DisableAuditing]
    public string Secret { get; set; } = "";
}

public sealed class Category : Entity<int>
{
    private Category() { }

    public Category(string name) => Name = name;

    public string Name { get; private set; } = "";
}

public sealed class Product : FullAuditedAggregateRoot<int>, IMultiTenant<Guid>
{
    private Product() { }

    public string Name { get; private set; } = "";
    public int Price { get; private set; }
    public int? CategoryId { get; private set; }
    public Category? Category { get; private set; }
    public Guid TenantId { get; set; }

    public static Product Create(string name, int price, Category? category = null)
    {
        var product = new Product { Name = name, Price = price, Category = category };
        product.RaiseDomainEvent(new ProductCreated(name));
        return product;
    }

    public void Discontinue() => RaiseDomainEvent(new ProductDiscontinued(Id, Name));

    public void ChangePrice(int price)
    {
        Price = price;
        RaiseDomainEvent(new ProductPriceChanged(Id, price));
    }
}

// ------------------------------------------------------------ DbContext

public sealed class TestDbContext(DbContextOptions<TestDbContext> options, ICurrentTenant currentTenant)
    : CanDbContext(options, currentTenant)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().HasOne(p => p.Category).WithMany().HasForeignKey(p => p.CategoryId);
        modelBuilder.AddCanOutbox();
        modelBuilder.AddCanAuditTrail();
        modelBuilder.AddCanInbox();
    }
}

// ------------------------------------------------------------ Sahte altyapı servisleri

public sealed class FakeCurrentUser : ICurrentUser
{
    public string? Id { get; set; } = "user-1";
    public string? UserName => Id;
    public string? Email => null;
    public IReadOnlyCollection<string> Roles => [];
}

public sealed class FakeCurrentTenant : ICurrentTenant
{
    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    public object? Id { get; set; } = TenantA;
}

public sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class EventLog
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries.ToArray();

    public void Add(string entry) => _entries.Enqueue(entry);
}

public sealed class ProductCreatedHandler(EventLog log) : INotificationHandler<ProductCreated>
{
    public Task Handle(ProductCreated notification, CancellationToken cancellationToken)
    {
        log.Add($"created:{notification.Name}");
        return Task.CompletedTask;
    }
}

/// <summary>Outbox handler'ının hata vermesini sağlamak için.</summary>
public sealed class FailureSwitch
{
    public bool Fail { get; set; }
}

public sealed class ProductDiscontinuedHandler(EventLog log, FailureSwitch failure) : INotificationHandler<ProductDiscontinued>
{
    public Task Handle(ProductDiscontinued notification, CancellationToken cancellationToken)
    {
        if (failure.Fail)
            throw new InvalidOperationException("handler hatası");

        log.Add($"discontinued:{notification.Name}");
        return Task.CompletedTask;
    }
}

public sealed class ProductPriceChangedHandler(EventLog log) : INotificationHandler<ProductPriceChanged>
{
    public Task Handle(ProductPriceChanged notification, CancellationToken cancellationToken)
    {
        log.Add($"price:{notification.Price}");
        return Task.CompletedTask;
    }
}

// ------------------------------------------------------------ Test ortamı

/// <summary>Her test için ayrı, bellekte çalışan bir SQLite veritabanı.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    private TestHost(SqliteConnection connection, ServiceProvider provider, FakeCurrentUser user, FakeCurrentTenant tenant, FixedClock clock, EventLog events)
    {
        _connection = connection;
        _provider = provider;
        User = user;
        Tenant = tenant;
        Clock = clock;
        Events = events;
    }

    public FakeCurrentUser User { get; }
    public FakeCurrentTenant Tenant { get; }
    public FixedClock Clock { get; }
    public EventLog Events { get; }

    public static async Task<TestHost> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var user = new FakeCurrentUser();
        var tenant = new FakeCurrentTenant();
        var clock = new FixedClock();
        var events = new EventLog();

        var services = new ServiceCollection();

        // AddCanPersistence varsayılanları TryAdd ile ekler; önce kaydedilen sahteler kalır.
        services.AddSingleton<ICurrentUser>(user);
        services.AddSingleton<ICurrentTenant>(tenant);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(events);
        services.AddSingleton(new FailureSwitch());
        services.AddLogging();

        services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<TestHost>());
        services.AddCanPersistence<TestDbContext>(options => options.UseSqlite(connection));
        configure?.Invoke(services);

        var host = new TestHost(connection, services.BuildServiceProvider(), user, tenant, clock, events);

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestDbContext>().Database.EnsureCreatedAsync();
        }

        return host;
    }

    public AsyncServiceScope CreateScope() => _provider.CreateAsyncScope();

    public IServiceProvider Services => _provider;

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
