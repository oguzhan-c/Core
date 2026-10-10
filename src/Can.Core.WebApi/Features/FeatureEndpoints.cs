using Can.Core.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Features;

public static class FeatureEndpointExtensions
{
    /// <summary>
    /// Endpoint (ya da grup) yalnızca bu özelliklerin hepsi açıksa çalışır; değilse 404 (<c>feature_disabled</c>).
    /// <c>AddCanFeatureFlags(...)</c> gerekir.
    /// </summary>
    /// <example><code>app.MapPost("/checkout/v2", Checkout).RequireFeature("new-checkout");</code></example>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, params string[] features)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(features);
        builder.AddEndpointFilter(async (context, next) =>
        {
            IFeatureFlags flags = context.HttpContext.RequestServices.GetRequiredService<IFeatureFlags>();
            foreach (string feature in features)
            {
                if (!await flags.IsEnabledAsync(feature, context.HttpContext.RequestAborted).ConfigureAwait(false))
                    return Disabled();
            }

            return await next(context).ConfigureAwait(false);
        });
        return builder;
    }

    /// <summary>
    /// O anki kullanıcı ve tenant için açık özellikleri döner: <c>{ "features": ["dark-mode", ...] }</c>. Arayüz menüleri
    /// buna göre gizlenir; asıl denetim yine sunucudadır (<see cref="RequireFeature{TBuilder}"/>, <c>IFeatureGatedRequest</c>).
    /// </summary>
    public static RouteHandlerBuilder MapCanFeatures(this IEndpointRouteBuilder endpoints, string pattern = "/api/features")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGet(pattern, async (IFeatureFlags flags, HttpContext http) =>
            TypedResults.Ok(new FeatureList(await flags.GetEnabledAsync(http.RequestAborted).ConfigureAwait(false))));
    }

    private static ProblemHttpResult Disabled()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status404NotFound,
            Title = ProblemTitles.NotFound,
            Detail = "Bu özellik kullanılamıyor.",
        };
        problem.Extensions["code"] = "feature_disabled";
        return TypedResults.Problem(problem);
    }

    /// <param name="Features">Açık özellikler.</param>
    public sealed record FeatureList(IReadOnlyList<string> Features);
}
