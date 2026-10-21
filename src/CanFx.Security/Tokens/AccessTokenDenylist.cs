namespace CanFx.Security.Tokens;

/// <summary>
/// Süresi dolmadan geri alınan access token'lar (<c>jti</c>). Çıkışta ya da tek bir oturum kapatılırken token buraya
/// yazılır; WebApi'de <c>UseCanAccessTokenRevocation()</c> her istekte bakar. Kayıt token'ın süresi dolunca kendiliğinden
/// silinir (liste büyümez). Uygulama: <c>CanFx.WebApi</c> → <c>AddCanAccessTokenRevocation()</c> (IDistributedCache;
/// çok sunucuda Redis).
/// </summary>
/// <remarks>
/// Kullanıcının TÜM oturumlarını kapatmak için (şifre değişti, hesap engellendi) güvenlik damgasını yenile; bu liste
/// tek token içindir.
/// </remarks>
public interface IAccessTokenDenylist
{
    /// <param name="tokenId">Token'ın <c>jti</c> claim'i.</param>
    /// <param name="expiresAt">Token'ın bitişi (<c>exp</c>); kayıt bu ana kadar tutulur.</param>
    /// <param name="cancellationToken">İptal.</param>
    Task RevokeAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    ValueTask<bool> IsRevokedAsync(string tokenId, CancellationToken cancellationToken = default);
}

/// <summary><see cref="IAccessTokenDenylist"/> anahtarları.</summary>
public static class AccessTokenDenylistKeys
{
    /// <summary>
    /// Bir oturumun (<c>sid</c>) tüm access token'larını geri almak için anahtar. Kayıt, o an verilmiş en uzun ömürlü
    /// access token'ın bitişine kadar (şimdi + <c>AccessTokenLifetime</c>) tutulmalı.
    /// </summary>
    /// <param name="sessionId">Oturum kimliği.</param>
    public static string Session(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return "sid:" + sessionId;
    }
}
