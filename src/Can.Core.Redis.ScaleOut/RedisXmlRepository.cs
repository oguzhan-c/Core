using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using StackExchange.Redis;

namespace Can.Core.Redis.ScaleOut;

/// <summary>
/// Data Protection anahtarlarını Redis listesinde tutar: tüm sunucular aynı anahtarlarla şifreler/çözer (2FA cookie'si,
/// OAuth dönüş bilgisi, passkey çerezi). Microsoft'un aynı işi yapan paketiyle aynı yöntem: anahtar başına bir liste öğesi.
/// </summary>
internal sealed class RedisXmlRepository(RedisConnection connection, string key) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() =>
        Database().ListRange(key).Select(value => XElement.Parse((string)value!)).ToArray();

    public void StoreElement(XElement element, string friendlyName) =>
        Database().ListRightPush(key, element.ToString(SaveOptions.DisableFormatting));

    // IXmlRepository senkron bir arayüz; bağlantı uygulama açılışında bir kez kurulur.
    private IDatabase Database() => connection.GetAsync().GetAwaiter().GetResult().GetDatabase();
}
