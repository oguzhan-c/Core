using CanFx.Settings;

namespace CanAppTemplate.Application.Services.Settings;

/// <summary>
/// ÇALIŞIRKEN değiştirilebilen uygulama ayarları (<c>CanFx.Settings</c>): kodda varsayılan değerle tanımlanır, yönetim uçlarından
/// (<c>/api/settings</c>) değiştirilir, veritabanında saklanır. <see cref="SettingDefinition.IsVisibleToClients"/> olanlar
/// giriş yapmadan da <c>/api/settings/client</c> ile okunur.
/// </summary>
/// <remarks>
/// <c>appsettings.json</c> ile karıştırma: orası açılışta bir kez okunan, değişince yeniden başlatma gereken yapılandırmadır
/// (bağlantı dizesi, JWT, Redis adresi ...); gizli değerler user-secrets'ta. Burada ise yönetici ekranından yeniden
/// başlatmadan değişen, global ya da tenant bazında farklı olabilen iş ayarları durur.
/// <para>TODO(can-gen): Uygulamanın ayarlarını buraya ekle ve <see cref="All"/> listesine yaz. Örnek ayarı silebilirsin.</para>
/// </remarks>
public static class RuntimeSettings
{
    public static readonly SettingKey<string> SupportEmail = new SettingKey<string>("app.support-email", "support@canapptemplate.local")
    {
        DisplayName = "Destek e-postası",
        Description = "Kullanıcılara gösterilen iletişim adresi.",
        Group = "Genel",
        Scopes = SettingScope.Global,
        IsVisibleToClients = true,
    };

    public static SettingDefinition[] All => [SupportEmail /* can-gen:settings */];
}
