using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;

namespace Can.Core.Security.Entities;

/// <summary>
/// İki adımlı doğrulama kurtarma kodu (yalnızca hash'i). Her kod bir kez kullanılır.
/// </summary>
/// <remarks>
/// Girişte: <c>IRecoveryCodeService.Hash(girilen)</c> ile kullanıcının kullanılmamış kodları arasında ara, bulunursa
/// <see cref="MarkUsed"/>. Kodlar yeniden üretilince ya da 2FA kapatılınca eskileri sil.
/// </remarks>
public class RecoveryCode<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected RecoveryCode()
    {
        UserId = default!;
        CodeHash = string.Empty;
    }

    public RecoveryCode(TId userId, string codeHash, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeHash);
        UserId = userId;
        CodeHash = codeHash;
        CreatedAt = createdAt;
    }

    public TId UserId { get; protected set; }

    [DisableAuditing]
    public string CodeHash { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset? UsedAt { get; protected set; }

    public bool IsUsed => UsedAt is not null;

    public void MarkUsed(DateTimeOffset now)
    {
        if (IsUsed)
            throw new InvalidOperationException("Kurtarma kodu zaten kullanılmış.");
        UsedAt = now;
    }
}
