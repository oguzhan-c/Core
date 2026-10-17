using Can.Core.Application;
using Can.Core.Domain.Results;

namespace CanAppTemplate.Application.Services.AuthService;

public static class CurrentUserExtensions
{
    /// <summary>Giriş yapmış kullanıcının kimliği; giriş yoksa 401.</summary>
    public static Result<Guid> UserId(this ICurrentUser currentUser) =>
        Guid.TryParse(currentUser.Id, out Guid userId) ? userId : Error.Unauthorized();
}
