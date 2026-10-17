using Can.Core.Settings;

namespace CanAppTemplate.Application.Services.Settings;

/// <summary>
/// Uygulama ayarları (<c>Can.Core.Settings</c>): kodda varsayılan değerle tanımlanır, yönetim uçlarından
/// (<c>/api/settings</c>) değiştirilir, veritabanında saklanır. <see cref="SettingDefinition.IsVisibleToClients"/> olanlar
/// giriş yapmadan da <c>/api/settings/client</c> ile okunur.
/// </summary>
/// <remarks>TODO(can-gen): Uygulamanın ayarlarını buraya ekle ve <see cref="All"/> listesine yaz. Örnek ayarı silebilirsin.</remarks>
public static class AppSettings
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
