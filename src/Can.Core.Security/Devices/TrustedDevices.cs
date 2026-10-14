namespace Can.Core.Security.Devices;

/// <summary>Kullanıcının kayıtlı cihazı (gösterim için; açık anahtar dışarı verilmez).</summary>
public sealed record TrustedDeviceInfo(string DeviceId, string Name, string? Platform, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
/// Kayıtlı cihazlar (<see cref="IDeviceBindingService"/> üzerine kalıcı kayıt). Uygulama:
/// <c>Can.Core.Security.EntityFrameworkCore</c> → <c>AddCanTrustedDevices&lt;TContext, TId&gt;()</c>. Akış:
/// <list type="number">
/// <item>Kayıt (kullanıcı giriş yapmış ve tercihen 2FA'yı geçmişken): cihaz anahtar çifti üretir, açık anahtarı
/// <see cref="RegisterAsync"/> ile kaydedilir.</item>
/// <item>Doğrulama (giriş, ödeme onayı ...): <see cref="CreateChallenge"/> → cihaz imzalar → <see cref="VerifyAsync"/>.</item>
/// <item>Kaldırma: <see cref="RevokeAsync"/>; o cihazın oturumları da kapanır.</item>
/// </list>
/// Hangi işlemin kayıtlı cihaz isteyeceği (yeni cihazda 2FA, ödemede imza ...) uygulamanın kararıdır.
/// </summary>
public interface ITrustedDeviceService
{
    /// <summary>
    /// Cihazı kaydeder; aynı kimlikle kayıt varsa anahtarı ve adı yenilenir (uygulama yeniden kuruldu).
    /// </summary>
    /// <exception cref="ArgumentException">Geçersiz açık anahtar.</exception>
    Task<TrustedDeviceInfo> RegisterAsync(string userId, string deviceId, string name, string? platform, string publicKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrustedDeviceInfo>> ListAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Cihaz için tek kullanımlık meydan okuma.</summary>
    DeviceChallenge CreateChallenge(string deviceId);

    /// <summary>
    /// İmzayı kullanıcının kayıtlı cihazıyla doğrular; geçerliyse meydan okuma "kullanıldı" olarak işaretlenir (tekrar
    /// kullanılamaz). Cihaz kayıtlı değilse, kaldırılmışsa ya da imza geçersizse <see langword="false"/>.
    /// </summary>
    Task<bool> VerifyAsync(string userId, string deviceId, string challenge, string signature, CancellationToken cancellationToken = default);

    /// <summary>Cihazı kaldırır ve o cihazdaki oturumları kapatır. Cihaz yoksa <see langword="false"/>.</summary>
    Task<bool> RevokeAsync(string userId, string deviceId, CancellationToken cancellationToken = default);
}
