using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Can.Core.Security.EntityFrameworkCore;

/// <summary>
/// Gizli değerin veritabanı biçimi: <c>"CAN1"</c> + şifreli içerik. Bu önek olmayan değerler (şifreleme açılmadan önce
/// kaydedilmiş) olduğu gibi okunur; böylece şifreleme sonradan açılabilir.
/// </summary>
public static class ProtectedSecret
{
    private static readonly byte[] Marker = "CAN1"u8.ToArray();

    internal static ValueConverter<byte[], byte[]> Converter(ISecretProtector protector) =>
        new(value => Protect(protector, value), stored => Unprotect(protector, stored));

    internal static readonly ValueComparer<byte[]> Comparer = new(
        (a, b) => BytesEqual(a, b),
        v => v.Length,
        v => Copy(v));

    // Karşılaştırıcı ifade ağacına dönüşür: Span orada kullanılamaz, yardımcı metot çağrılır
    private static bool BytesEqual(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    private static byte[] Copy(byte[] value) => [.. value];

    /// <summary>Şifrelenmiş mi (önek var mı).</summary>
    public static bool IsProtected(byte[] stored) => stored.AsSpan().StartsWith(Marker);

    internal static byte[] Protect(ISecretProtector protector, byte[] value) => [.. Marker, .. protector.Protect(value)];

    internal static byte[] Unprotect(ISecretProtector protector, byte[] stored) =>
        IsProtected(stored) ? protector.Unprotect(stored[Marker.Length..]) : stored;

    /// <summary>
    /// Şifreleme açıldıktan sonra bir kez çağır (bakım işi ya da açılış): tüm TOTP anahtarları yeniden yazılır; düz
    /// kaydedilmiş olanlar şifrelenir, şifreli olanlar güncel anahtarla yeniden şifrelenir. Yazılan kayıt sayısını döner.
    /// </summary>
    public static async Task<int> ProtectExistingOtpSecretsAsync<TId>(DbContext context, CancellationToken cancellationToken = default)
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(context);
        int changed = 0;
        foreach (Entities.OtpAuthenticator<TId> authenticator in await context.Set<Entities.OtpAuthenticator<TId>>().IgnoreQueryFilters().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            context.Entry(authenticator).Property(a => a.SecretKey).IsModified = true;
            changed++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }
}
