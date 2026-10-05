using Can.Core.Persistence.Dynamic;

namespace Northwind.WebApi.Endpoints;

/// <summary>
/// Dinamik filtrelemeyi denemek için hazır örnekler. Her örneğin <c>body</c>'sini ilgili <c>endpoint</c>'e POST et
/// (Swagger'da "Try it out" → gövdeye yapıştır).
/// </summary>
internal static class SearchExampleEndpoints
{
    public sealed record SearchExample(string Title, string Endpoint, DynamicQuery Body);

    public static void MapSearchExampleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/search/examples", () => new
            {
                Operators = new[]
                {
                    "eq", "neq", "lt", "lte", "gt", "gte", "isnull", "isnotnull",
                    "startswith", "endswith", "contains", "doesnotcontain", "in", "between",
                },
                Notes = new[]
                {
                    "Alan adları büyük/küçük harf duyarsız; iç içe alanlar noktayla: address.country, shipAddress.city.",
                    "Metin karşılaştırmaları varsayılan olarak büyük/küçük harf duyarsız (caseSensitive: true ile değişir).",
                    "'in' için virgülle ayrılmış liste, 'between' için 'alt,üst' verilir.",
                    "logic: 'and' (varsayılan) ya da 'or'; filters ile istenildiği kadar iç içe grup kurulabilir.",
                    "Geçersiz alan/operatör/değer 400 döner.",
                },
                Examples = Examples,
            })
            .WithTags("Search")
            .WithSummary("Dinamik filtreleme örnekleri ve operatör listesi.")
            .AllowAnonymous();
    }

    private static readonly SearchExample[] Examples =
    [
        new(
            "Fiyatı 20 ile 50 arasında, satışta olan ürünler, pahalıdan ucuza",
            "POST /api/products/search",
            new DynamicQuery(
                [new Sort("unitPrice", "desc")],
                new Filter
                {
                    Logic = "and",
                    Filters =
                    [
                        new Filter("unitPrice", FilterOperators.Between, "20,50"),
                        new Filter("isDiscontinued", FilterOperators.Equal, "false"),
                    ],
                }
            )
        ),
        new(
            "Adında 'chef' geçen ya da stoğu bitmiş ürünler",
            "POST /api/products/search",
            new DynamicQuery(
                [new Sort("name")],
                new Filter
                {
                    Logic = "or",
                    Filters = [new Filter("name", FilterOperators.Contains, "chef"), new Filter("unitsInStock", FilterOperators.Equal, "0")],
                }
            )
        ),
        new(
            "İç içe gruplar: satışta olan VE (adında 'sauce' geçen VEYA (fiyatı 10-30 arası VE stoğu 20'den az) VEYA şişe ile satılan)",
            "POST /api/products/search",
            new DynamicQuery(
                [new Sort("unitPrice", "desc"), new Sort("name")],
                new Filter
                {
                    Logic = "and",
                    Filters =
                    [
                        new Filter("isDiscontinued", FilterOperators.Equal, "false"),
                        new Filter
                        {
                            Logic = "or",
                            Filters =
                            [
                                new Filter("name", FilterOperators.Contains, "sauce"),
                                new Filter
                                {
                                    Logic = "and",
                                    Filters =
                                    [
                                        new Filter("unitPrice", FilterOperators.Between, "10,30"),
                                        new Filter("unitsInStock", FilterOperators.LessThan, "20"),
                                    ],
                                },
                                new Filter("quantityPerUnit", FilterOperators.Contains, "bottles"),
                            ],
                        },
                    ],
                }
            )
        ),
        new(
            "Almanya veya Fransa'daki müşteriler, şehre göre",
            "POST /api/customers/search",
            new DynamicQuery([new Sort("address.city")], new Filter("address.country", FilterOperators.In, "Germany,France"))
        ),
        new(
            "Faksı olmayan, yetkilisi 'Owner' olan müşteriler",
            "POST /api/customers/search",
            new DynamicQuery(
                null,
                new Filter
                {
                    Filters = [new Filter("fax", FilterOperators.IsNull), new Filter("contactTitle", FilterOperators.Equal, "Owner")],
                }
            )
        ),
        new(
            "1997'de verilmiş, kargo ücreti 100'den yüksek siparişler",
            "POST /api/orders/search",
            new DynamicQuery(
                [new Sort("freight", "desc")],
                new Filter
                {
                    Filters =
                    [
                        new Filter("orderedAt", FilterOperators.Between, "1997-01-01T00:00:00Z,1997-12-31T23:59:59Z"),
                        new Filter("freight", FilterOperators.GreaterThan, "100"),
                    ],
                }
            )
        ),
        new(
            "Henüz kargoya verilmemiş, Brezilya'ya gidecek siparişler",
            "POST /api/orders/search",
            new DynamicQuery(
                [new Sort("number")],
                new Filter
                {
                    Filters = [new Filter("status", FilterOperators.Equal, "Placed"), new Filter("shipAddress.country", FilterOperators.Equal, "Brazil")],
                }
            )
        ),
    ];
}
