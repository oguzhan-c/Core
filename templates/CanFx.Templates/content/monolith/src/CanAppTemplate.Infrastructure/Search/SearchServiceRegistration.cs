using CanFx.Search;
using CanFx.Search.Elasticsearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CanAppTemplate.Infrastructure.Search;

/// <summary>Arama: <c>Search:Elasticsearch:Url</c> doluysa Elasticsearch, yoksa bellek içi motor.</summary>
/// <remarks>TODO(can-gen): Aranacak belgeleri ve dizinleri tanımla (CanFx.Search).</remarks>
public static class SearchServiceRegistration
{
    public static IServiceCollection AddSearch(this IServiceCollection services, IConfiguration config)
    {
        CanSearchBuilder search = services.AddCanSearch();
        if (config["Search:Elasticsearch:Url"] is { Length: > 0 })
            search.UseElasticsearch(o => config.GetSection("Search:Elasticsearch").Bind(o));
        return services;
    }
}
