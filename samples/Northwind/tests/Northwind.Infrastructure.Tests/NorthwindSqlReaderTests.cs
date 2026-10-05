using Northwind.Infrastructure.Seeding;

namespace Northwind.Infrastructure.Tests;

public class NorthwindSqlReaderTests
{
    private static readonly NorthwindSqlReader Sql = NorthwindSqlReader.Load();

    [Theory]
    [InlineData("categories", 8)]
    [InlineData("suppliers", 29)]
    [InlineData("products", 77)]
    [InlineData("customers", 91)]
    [InlineData("employees", 9)]
    [InlineData("employee_territories", 49)]
    [InlineData("shippers", 6)]
    [InlineData("orders", 830)]
    [InlineData("order_details", 2155)]
    [InlineData("region", 4)]
    [InlineData("territories", 53)]
    public void Embedded_script_contains_all_rows(string table, int count)
    {
        Assert.Equal(count, Sql.Rows(table).Count);
    }

    [Fact]
    public void Values_are_parsed_with_quotes_nulls_and_reals()
    {
        NorthwindSqlReader.Row order = Sql.Rows("orders").First(r => r.Int(0) == 10248);
        Assert.Equal("VINET", order.RequiredText(1));
        Assert.Equal("59 rue de l'Abbaye", order.Text(9)); // '' kaçışı
        Assert.Null(order.Text(11)); // NULL
        Assert.Equal(32.38m, order.Decimal(7)); // real 32.3800011
        Assert.Equal(new DateOnly(1996, 7, 4), order.Date(3));

        NorthwindSqlReader.Row line = Sql.Rows("order_details").First(r => r.Int(0) == 10248 && r.Int(1) == 42);
        Assert.Equal(9.8m, line.Decimal(2)); // 9.80000019
    }

    [Fact]
    public void Empty_binary_values_are_treated_as_missing()
    {
        NorthwindSqlReader.Row employee = Sql.Rows("employees").First(r => r.Int(0) == 1);
        Assert.Equal("Nancy", employee.RequiredText(2));
        Assert.Null(employee.Text(14)); // photo '\x'
        Assert.Contains('\n', employee.RequiredText(7)); // "\n" satır sonuna çevrilir
    }
}
