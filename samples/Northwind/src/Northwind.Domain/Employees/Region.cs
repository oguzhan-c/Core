using Can.Core.Domain.Entities;

namespace Northwind.Domain.Employees;

/// <summary>Satış bölgesi. Tüm mağazaların ortak kullandığı referans verisi (tenant'a ait değil).</summary>
public sealed class Region : Entity<int>
{
    private Region()
    {
        Description = string.Empty;
    }

    public Region(int id, string description)
        : base(id)
    {
        Description = description.Trim();
    }

    public string Description { get; private set; }
}

/// <summary>Bölgedeki satış alanı; kimliği posta kodu benzeri bir koddur (ör. <c>01581</c>). Ortak referans verisi.</summary>
public sealed class Territory : Entity<string>
{
    private Territory()
    {
        Description = string.Empty;
    }

    public Territory(string code, string description, int regionId)
        : base(code)
    {
        Description = description.Trim();
        RegionId = regionId;
    }

    public string Description { get; private set; }

    public int RegionId { get; private set; }
}
