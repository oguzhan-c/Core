using Can.Core.Application;
using Can.Core.Domain.Results;

namespace CanAppTemplate.Application.Services.AuthService;

public static class CurrentUserExtensions
{
    /// <summary>Giriş yapmış kullanıcının kimliği; giriş yoksa 401.</summary>
    public static Result<IdentityKey> UserId(this ICurrentUser currentUser) =>
        IdentityKey.TryParse(currentUser.Id, out IdentityKey userId) ? userId : Error.Unauthorized();
}
