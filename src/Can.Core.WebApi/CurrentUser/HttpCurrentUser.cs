using System.Security.Claims;
using Can.Core.Application;
using Microsoft.AspNetCore.Http;

namespace Can.Core.WebApi.CurrentUser;

/// <summary>
/// <see cref="ICurrentUser"/>'ı o anki isteğin DOĞRULANMIŞ kullanıcısından (authentication middleware'in
/// token'ı doğrulayıp oluşturduğu <see cref="ClaimsPrincipal"/>) okur. Değerler her erişimde o anki
/// istekten okunur; istekler arasında bilgi taşınmaz.
/// </summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly CanWebApiOptions _options;

    public HttpCurrentUser(IHttpContextAccessor httpContextAccessor, CanWebApiOptions options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    /// <summary>Yalnızca kimliği doğrulanmış kullanıcı; anonim isteklerde <see langword="null"/>.</summary>
    private ClaimsPrincipal? User =>
        _httpContextAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;

    public string? Id => FindFirst(_options.UserIdClaimTypes);

    public string? UserName => FindFirst(_options.UserNameClaimTypes);

    public string? Email => FindFirst(_options.EmailClaimTypes);

    public IReadOnlyCollection<string> Roles => ClaimHelper.FindAll(User, _options.RoleClaimTypes);

    public IReadOnlyCollection<string> Permissions => ClaimHelper.FindAll(User, _options.PermissionClaimTypes);

    private string? FindFirst(string[] claimTypes) => ClaimHelper.FindFirst(User, claimTypes);
}

internal static class ClaimHelper
{
    public static string? FindFirst(ClaimsPrincipal? user, string[] claimTypes)
    {
        if (user is null)
            return null;

        foreach (string claimType in claimTypes)
        {
            string? value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    public static IReadOnlyCollection<string> FindAll(ClaimsPrincipal? user, string[] claimTypes)
    {
        if (user is null)
            return [];

        return user.Claims
            .Where(c => claimTypes.Contains(c.Type, StringComparer.Ordinal) && !string.IsNullOrWhiteSpace(c.Value))
            .Select(c => c.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
