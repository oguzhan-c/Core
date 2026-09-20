namespace Domain.MultiTenancy;

public interface ITenantScoped<TId>
{
    TId TenantId { get; set; }
}