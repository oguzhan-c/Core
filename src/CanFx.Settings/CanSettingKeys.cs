namespace CanFx.Settings;

/// <summary>
/// CanFx'in yönetim ekranından değiştirilebilen hazır ayarları. Hiçbiri kendiliğinden kayıtlı değildir: kullanmak
/// istediklerini <c>AddCanSettings(s =&gt; s.Add(CanSettingKeys.All))</c> ile ekle ve değeri ilgili yerde oku
/// (örnekler her ayarın açıklamasında). Güvenlik ayarları yalnızca uygulama genelinde değiştirilebilir: bir tenant
/// yöneticisi kendi tenant'ı için güvenliği gevşetemez.
/// </summary>
public static class CanSettingKeys
{
    /// <summary>Giriş güvenliği (<c>ILoginThrottle</c> ve <c>User.RegisterFailedAccess</c> ile kullanılır).</summary>
    public static class Login
    {
        private const string GroupName = "Giriş güvenliği";

        /// <summary><c>new LoginThrottleLimits(maxAttempts, window, lockout)</c> → <c>throttle.RegisterFailureAsync(..., limits)</c>.</summary>
        public static readonly SettingKey<int> MaxAttemptsPerIp = new SettingKey<int>("security.login.max-attempts-per-ip", 5)
        {
            DisplayName = "Aynı IP'den en fazla hatalı giriş",
            Description = "Bu kadar hatalı denemeden sonra o IP, o hesap için engellenir.",
            Group = GroupName,
            Scopes = SettingScope.Global,
        }.Validate(v => v is >= 1 and <= 100, "1 ile 100 arasında olmalı.");

        /// <summary><c>user.RegisterFailedAccess(now, accountMaxAttempts, lockout)</c>.</summary>
        public static readonly SettingKey<int> AccountMaxAttempts = new SettingKey<int>("security.login.account-max-attempts", 20)
        {
            DisplayName = "Hesap başına en fazla hatalı giriş (tüm IP'ler)",
            Description = "Ulaşılınca hesap herkes için kilitlenir; çok IP'li saldırılara karşı.",
            Group = GroupName,
            Scopes = SettingScope.Global,
        }.Validate(v => v is >= 1 and <= 1000, "1 ile 1000 arasında olmalı.");

        public static readonly SettingKey<int> LockoutMinutes = new SettingKey<int>("security.login.lockout-minutes", 15)
        {
            DisplayName = "Kilit süresi (dakika)",
            Group = GroupName,
            Scopes = SettingScope.Global,
        }.Validate(v => v is >= 1 and <= 1440, "1 ile 1440 (24 saat) arasında olmalı.");
    }

    /// <summary>Dosya yükleme (<c>FileUploadPolicy.Create(maxSizeMb, extensions)</c> → <c>validator.ValidateAsync(..., policy)</c>).</summary>
    public static class Upload
    {
        private const string GroupName = "Dosya yükleme";

        public static readonly SettingKey<int> MaxSizeMegabytes = new SettingKey<int>("files.upload.max-size-mb", 10)
        {
            DisplayName = "En büyük dosya boyutu (MB)",
            Group = GroupName,
        }.Validate(v => v is >= 1 and <= 2048, "1 ile 2048 arasında olmalı.");

        /// <summary>
        /// Seçilebilecek uzantılar burada sabittir; çalıştırılabilir türler listede yoktur ve ayardan açılamaz.
        /// </summary>
        public static readonly SettingKey<string[]> AllowedExtensions = Extensions();

        private static SettingKey<string[]> Extensions()
        {
            SettingKey<string[]> key = SettingKeys.OptionList(
                "files.upload.allowed-extensions",
                [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".pdf", ".txt", ".csv", ".json", ".xml", ".zip", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".mp3", ".mp4"],
                enabledByDefault: [".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf", ".txt", ".csv", ".docx", ".xlsx", ".pptx"],
                displayName: "Kabul edilen dosya türleri",
                group: GroupName);
            return key.Validate(v => v.Length > 0, "En az bir dosya türü seçilmeli.");
        }
    }

    /// <summary>Hepsi (<c>AddCanSettings(s =&gt; s.Add(CanSettingKeys.All))</c>).</summary>
    public static SettingDefinition[] All =>
        [Login.MaxAttemptsPerIp, Login.AccountMaxAttempts, Login.LockoutMinutes, Upload.MaxSizeMegabytes, Upload.AllowedExtensions];
}
