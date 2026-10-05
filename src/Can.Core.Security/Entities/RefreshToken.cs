using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;
using Can.Core.Security.Tokens;

namespace Can.Core.Security.Entities;

/// <summary>
/// Refresh token kaydı. Token'ın kendisi değil SHA-256 hash'i saklanır.
/// </summary>
/// <remarks>
/// Önerilen akış (rotasyon):
/// <list type="number">
/// <item>İstemci refresh token gönderir → <c>ITokenService.HashRefreshToken</c> ile hash'lenip aranır.</item>
/// <item>Kayıt aktifse (<see cref="IsActive"/>): yeni token üretilir, eskisi
/// <see cref="Revoke"/> ile "yenisiyle değiştirildi" olarak kapatılır.</item>
/// <item>Kayıt bulunur ama zaten iptal edilmişse token çalınmış olabilir: kullanıcının TÜM aktif
/// refresh token'larını iptal et (yeniden kullanım tespiti).</item>
/// </list>
/// </remarks>
public class RefreshToken<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected RefreshToken()
    {
        UserId = default!;
        TokenHash = string.Empty;
    }

    public RefreshToken(TId userId, RefreshTokenValue token, DateTimeOffset createdAt, string? createdByIp)
    {
        ArgumentNullException.ThrowIfNull(token);

        UserId = userId;
        TokenHash = token.TokenHash;
        ExpiresAt = token.ExpiresAt;
        CreatedAt = createdAt;
        CreatedByIp = createdByIp;
    }

    public TId UserId { get; protected set; }

    [DisableAuditing]
    public string TokenHash { get; protected set; }

    public DateTimeOffset ExpiresAt { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public string? CreatedByIp { get; protected set; }

    public DateTimeOffset? RevokedAt { get; protected set; }

    public string? RevokedByIp { get; protected set; }

    public string? RevokedReason { get; protected set; }

    /// <summary>Rotasyonda bunun yerine verilen token'ın hash'i (token zincirini izlemek için).</summary>
    [DisableAuditing]
    public string? ReplacedByTokenHash { get; protected set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);

    public void Revoke(DateTimeOffset now, string? ip, string reason, string? replacedByTokenHash = null)
    {
        if (IsRevoked)
            return;

        RevokedAt = now;
        RevokedByIp = ip;
        RevokedReason = reason;
        ReplacedByTokenHash = replacedByTokenHash;
    }
}
