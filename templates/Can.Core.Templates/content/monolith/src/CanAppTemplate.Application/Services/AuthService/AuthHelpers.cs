using Can.Core.Domain.Results;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using Microsoft.EntityFrameworkCore;
using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Events;
using Can.Core.Security.Sessions;
using FluentValidation;
using Can.Core.Security.Hashing;
using Can.Core.Mailing;
using Can.Core.Security.VerificationCodes;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Services.AuthService;

internal static class EmailMask
{
    /// <summary><c>ornek@gmail.com</c> → <c>or***@gmail.com</c>.</summary>
    public static string Mask(string email)
    {
        int at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
            return email;

        int visible = Math.Min(2, at);
        return string.Concat(email.AsSpan(0, visible), new string('*', Math.Max(1, at - visible)), email.AsSpan(at));
    }
}

internal static class ExternalNames
{
    /// <summary>Sağlayıcının verdiği adlardan ad/soyad; yoksa tam addan ya da e-postanın kullanıcı adı kısmından.</summary>
    public static (string FirstName, string LastName) Split(string? givenName, string? familyName, string? fullName, string email)
    {
        string first = givenName?.Trim() ?? string.Empty;
        string last = familyName?.Trim() ?? string.Empty;

        if (first.Length == 0 && !string.IsNullOrWhiteSpace(fullName))
        {
            string[] parts = fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            first = parts.Length > 1 ? string.Join(' ', parts[..^1]) : parts[0];
            if (last.Length == 0 && parts.Length > 1)
                last = parts[^1];
        }

        if (first.Length == 0)
            first = email[..Math.Max(1, email.IndexOf('@', StringComparison.Ordinal))];

        if (last.Length == 0)
            last = "-";

        return (Truncate(first), Truncate(last));
    }

    private static string Truncate(string value) => value.Length <= AppUser.NameMaxLength ? value : value[..AppUser.NameMaxLength];
}

/// <summary>Girişlerin ortak adımları ve güvenlik olayları.</summary>
internal static class AuthEvents
{
    /// <summary>
    /// Oturumu açar; cihaz daha önce görülmediyse "yeni cihazdan giriş" olayı yayınlanır (e-posta bildirimi bu olaya
    /// bağlıdır). Cihaz kimliği gönderilmeyen istemcilerde (tarayıcı ilk kez) olay yayınlanmaz.
    /// </summary>
    public static async Task<AuthResult> SignInAsync(
        AppUser user,
        ClientInfo client,
        string method,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        CancellationToken cancellationToken)
    {
        bool newDevice = client.DeviceId is { Length: > 0 } deviceId
            && !await sessions.IsKnownDeviceAsync(user.Id.ToString(), deviceId, cancellationToken);

        AuthResult result = await tokenIssuer.IssueAsync(user, client, cancellationToken);

        var properties = new Dictionary<string, string?> { ["method"] = method };
        await events.PublishAsync(
            new SecurityEvent(SecurityEventTypes.LoginSucceeded, user.Id.ToString()) { SessionId = result.SessionId, DeviceId = client.DeviceId, Properties = properties },
            cancellationToken
        );

        if (newDevice)
        {
            await events.PublishAsync(
                new SecurityEvent(SecurityEventTypes.LoginFromNewDevice, user.Id.ToString())
                {
                    SessionId = result.SessionId,
                    DeviceId = client.DeviceId,
                    Properties = new Dictionary<string, string?> { ["deviceName"] = client.DeviceName, ["method"] = method },
                },
                cancellationToken
            );
        }

        return result;
    }

    /// <summary>Başarısız deneme; hesap bu denemeyle kilitlendiyse ayrıca kilitlenme olayı.</summary>
    public static async Task FailedAsync(ISecurityEvents events, AppUser user, DateTimeOffset now, string reason, CancellationToken cancellationToken, string type = SecurityEventTypes.LoginFailed)
    {
        await events.PublishAsync(new SecurityEvent(type, user.Id.ToString()) { Reason = reason }, cancellationToken);

        if (user.IsLockedOut(now))
            await events.PublishAsync(new SecurityEvent(SecurityEventTypes.AccountLocked, user.Id.ToString()) { Reason = reason }, cancellationToken);
    }
}

internal static class DefaultRoles
{
    /// <summary>Seed eksikse bu bir kurulum hatasıdır (beklenmeyen): exception olarak kalır.</summary>
    public static async Task<Role<IdentityKey>> GetAsync(IRepository<Role<IdentityKey>, IdentityKey> roles, CancellationToken cancellationToken) =>
        await roles.GetAsync(r => r.Name == Roles.User, cancellationToken: cancellationToken)
        ?? throw new InvalidOperationException("User rolü bulunamadı; seed çalıştı mı?");
}
