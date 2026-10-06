using System.Reflection;

namespace Can.Core.Localization;

public sealed class CanLocalizationOptions
{
    /// <summary>Çevirisi bulunamayan kültürlerin düştüğü kültür ve isteklerin varsayılanı.</summary>
    public string DefaultCulture { get; set; } = "tr";

    /// <summary>İstemcinin seçebileceği kültürler (<c>Accept-Language</c>, <c>?culture=</c>, cookie).</summary>
    public string[] SupportedCultures { get; set; } = ["tr", "en"];

    /// <summary>
    /// Uygulamanın çeviri klasörü (<c>tr.json</c>, <c>en.json</c> ...). Göreli yol uygulama klasörüne göredir.
    /// Bu klasördeki metinler paketlerin gömülü metinlerini ezer. Boşsa klasör okunmaz.
    /// </summary>
    public string? ResourcesPath { get; set; } = "Localization";

    /// <summary>
    /// Gömülü çevirileri okunacak assembly'ler (<c>*.tr.json</c> gibi EmbeddedResource'lar). Sonra eklenen öncekini ezer.
    /// Kaynaklar <c>WithCulture="false"</c> ile eklenmeli; yoksa MSBuild onları uydu (satellite) assembly'ye taşır.
    /// </summary>
    public List<Assembly> ResourceAssemblies { get; } = [];
}
