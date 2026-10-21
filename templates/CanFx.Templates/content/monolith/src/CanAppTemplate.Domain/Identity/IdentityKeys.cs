namespace CanAppTemplate.Domain.Identity;

/// <summary>
/// Kimlik tablolarının (kullanıcı, rol, yetki, oturum, passkey ...) anahtarı. Tip TEK yerde seçilir: kökteki
/// <c>Directory.Build.props</c> → <c>&lt;Using Include="System.Guid" Alias="IdentityKey" /&gt;</c> (Guid, Int32 ya da Int64).
/// Kod her yerde <c>IdentityKey</c> kullanır; tipi değiştirince yalnızca o satır ve bir migration gerekir.
/// </summary>
public static class IdentityKeys
{
    /// <summary>
    /// <see langword="true"/>: anahtarı veritabanı üretir (int / long). Yeni kaydın Id'si ancak kaydedildikten sonra
    /// belli olur; Id'ye ihtiyaç duyan bağlı kayıtlardan (oturum, doğrulama kodu ...) önce kaydet: <see cref="New"/>.
    /// </summary>
    public static bool IsDatabaseGenerated { get; } = typeof(IdentityKey) != typeof(Guid);

    /// <summary>
    /// Yeni kayıt için anahtar: Guid ise kodda üretilir (v7: zamana göre sıralı, index dostu); int / long ise
    /// <see langword="default"/> döner ve veritabanı üretir.
    /// </summary>
    public static IdentityKey New() => IsDatabaseGenerated ? default : (IdentityKey)(object)Guid.CreateVersion7();
}
