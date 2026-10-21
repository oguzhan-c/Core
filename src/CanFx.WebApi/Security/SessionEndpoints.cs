using CanFx.Application;
using CanFx.Security.Sessions;
using CanFx.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.WebApi.Security;

public static class SessionEndpointExtensions
{
    /// <summary>
    /// "Aktif oturumlarım" uç noktaları (giriş yapmış kullanıcının kendi oturumları):
    /// <list type="bullet">
    /// <item><c>GET {pattern}</c>: oturumlar, "bu cihaz" işaretli (<c>isCurrent</c>).</item>
    /// <item><c>DELETE {pattern}/{sessionId}</c>: o cihazdan çıkış. Kendi oturumuysa cookie'ler de silinir. Yoksa 404
    /// <c>session_not_found</c>.</item>
    /// <item><c>POST {pattern}/revoke-others</c>: diğer tüm cihazlardan çıkış; <c>{ "revoked": 2 }</c>.</item>
    /// </list>
    /// <c>AddCanUserSessions&lt;TContext, TId&gt;()</c> gerekir. Kapatılan oturumun access token'ının da hemen reddedilmesi
    /// için <c>UseCanAccessTokenRevocation()</c>. Dönen gruba politika eklenebilir (<c>.RequireAuthorization(...)</c>).
    /// </summary>
    public static RouteGroupBuilder MapCanSessions(this IEndpointRouteBuilder endpoints, string pattern = "/api/sessions")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder group = endpoints.MapGroup(pattern);

        group.MapGet("/", async Task<Results<Ok<SessionList>, ProblemHttpResult>> (HttpContext http, ICurrentUser user, IUserSessionService sessions) =>
        {
            if (user.Id is not { } userId)
                return Unauthorized();
            IReadOnlyList<UserSession> list = await sessions.ListAsync(userId, http.GetSessionId(), http.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(new SessionList(list));
        });

        group.MapDelete("/{sessionId}", async Task<Results<NoContent, ProblemHttpResult>> (string sessionId, HttpContext http, ICurrentUser user, IUserSessionService sessions) =>
        {
            if (user.Id is not { } userId)
                return Unauthorized();
            if (!await sessions.RevokeAsync(userId, sessionId, http.RequestAborted).ConfigureAwait(false))
                return Problem(StatusCodes.Status404NotFound, ProblemTitles.NotFound, "session_not_found", "Oturum bulunamadı.");

            if (string.Equals(sessionId, http.GetSessionId(), StringComparison.Ordinal))
                http.RequestServices.GetService<IAuthCookieService>()?.Clear(http.Response); // kendi oturumu: bu cihazdan da çıkış

            return TypedResults.NoContent();
        });

        group.MapPost("/revoke-others", async Task<Results<Ok<RevokedCount>, ProblemHttpResult>> (HttpContext http, ICurrentUser user, IUserSessionService sessions) =>
        {
            if (user.Id is not { } userId)
                return Unauthorized();
            if (http.GetSessionId() is not { } current)
                return Problem(StatusCodes.Status400BadRequest, ProblemTitles.Validation, "session_unknown", "Bu oturumun kimliği yok; tekrar giriş yap.");

            int revoked = await sessions.RevokeOthersAsync(userId, current, http.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(new RevokedCount(revoked));
        });

        return group;
    }

    private static ProblemHttpResult Unauthorized() =>
        Problem(StatusCodes.Status401Unauthorized, ProblemTitles.Unauthorized, "unauthorized", "Giriş yapılmamış.");

    private static ProblemHttpResult Problem(int status, string title, string code, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        return TypedResults.Problem(problem);
    }

    /// <param name="Sessions">Açık oturumlar.</param>
    public sealed record SessionList(IReadOnlyList<UserSession> Sessions);

    /// <param name="Revoked">Kapatılan oturum sayısı.</param>
    public sealed record RevokedCount(int Revoked);
}
