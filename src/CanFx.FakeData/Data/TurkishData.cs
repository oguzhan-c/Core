namespace CanFx.FakeData.Data;

/// <summary>
/// Türkçe veri listeleri. CanFx için derlendi (yaygın adlar, il / ilçe adları, genel kelimeler); başka bir kütüphaneden
/// kopyalanmadı. Gerçek kişi, şirket ya da adresle eşleşme tesadüfidir: e-posta alanları ayrılmış örnek alan adlarıdır.
/// </summary>
internal static class TurkishData
{
    public static readonly string[] FemaleFirstNames =
    [
        "Ayşe", "Fatma", "Emine", "Hatice", "Zeynep", "Elif", "Meryem", "Şerife", "Zehra", "Sultan", "Hanife", "Merve",
        "Havva", "Zeliha", "Esra", "Fadime", "Özlem", "Hacer", "Emel", "Yasemin", "Hülya", "Leyla", "Büşra", "Gül",
        "Derya", "Sevgi", "Ebru", "Kübra", "Tuğba", "Ece", "Selin", "Deniz", "Damla", "Gizem", "Buse", "Irmak", "Defne",
        "Nehir", "Asya", "Eylül", "Ela", "Duru", "Azra", "İrem", "Melek", "Sibel", "Pınar", "Gamze", "Seda", "Burcu",
        "Aslı", "Ceren", "Dilara", "Nur", "Sena", "Beyza", "Şeyma", "Rabia", "Cansu", "Yağmur",
    ];

    public static readonly string[] MaleFirstNames =
    [
        "Mehmet", "Mustafa", "Ahmet", "Ali", "Hüseyin", "Hasan", "İbrahim", "İsmail", "Osman", "Yusuf", "Murat", "Ömer",
        "Ramazan", "Halil", "Süleyman", "Abdullah", "Mahmut", "Recep", "Salih", "Fatih", "Kemal", "Kadir", "Emre",
        "Burak", "Serkan", "Hakan", "Onur", "Volkan", "Can", "Cem", "Kerem", "Eren", "Barış", "Tolga", "Umut", "Oğuz",
        "Oğuzhan", "Yiğit", "Arda", "Emir", "Efe", "Mert", "Berk", "Kaan", "Alp", "Batuhan", "Furkan", "Enes", "Tuncay",
        "Sinan", "Erkan", "Gökhan", "Levent", "Serdar", "Cenk", "Doruk", "Kağan", "Taha", "Metehan", "Uğur",
    ];

    public static readonly string[] LastNames =
    [
        "Yılmaz", "Kaya", "Demir", "Şahin", "Çelik", "Yıldız", "Yıldırım", "Öztürk", "Aydın", "Özdemir", "Arslan", "Doğan",
        "Kılıç", "Aslan", "Çetin", "Kara", "Koç", "Kurt", "Özkan", "Şimşek", "Polat", "Özcan", "Korkmaz", "Çakır",
        "Erdoğan", "Yavuz", "Can", "Acar", "Şen", "Aktaş", "Güler", "Yalçın", "Güneş", "Bozkurt", "Bulut", "Keskin",
        "Ünal", "Turan", "Gül", "Özer", "Işık", "Kaplan", "Avcı", "Sarı", "Tekin", "Taş", "Köse", "Yüksel", "Ateş",
        "Aksoy", "Uçar", "Altun", "Karaca", "Erdem", "Tunç", "Sevim", "Duman", "Coşkun", "Kocabaş", "Akın", "Uysal",
        "Ekinci", "Bayram", "Ceylan", "Tuna", "Karakaya", "Akbulut", "Önal", "Gündüz", "Toprak",
    ];

    /// <summary>81 il.</summary>
    public static readonly string[] Cities =
    [
        "Adana", "Adıyaman", "Afyonkarahisar", "Ağrı", "Amasya", "Ankara", "Antalya", "Artvin", "Aydın", "Balıkesir",
        "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur", "Bursa", "Çanakkale", "Çankırı", "Çorum", "Denizli",
        "Diyarbakır", "Edirne", "Elazığ", "Erzincan", "Erzurum", "Eskişehir", "Gaziantep", "Giresun", "Gümüşhane",
        "Hakkari", "Hatay", "Isparta", "Mersin", "İstanbul", "İzmir", "Kars", "Kastamonu", "Kayseri", "Kırklareli",
        "Kırşehir", "Kocaeli", "Konya", "Kütahya", "Malatya", "Manisa", "Kahramanmaraş", "Mardin", "Muğla", "Muş",
        "Nevşehir", "Niğde", "Ordu", "Rize", "Sakarya", "Samsun", "Siirt", "Sinop", "Sivas", "Tekirdağ", "Tokat",
        "Trabzon", "Tunceli", "Şanlıurfa", "Uşak", "Van", "Yozgat", "Zonguldak", "Aksaray", "Bayburt", "Karaman",
        "Kırıkkale", "Batman", "Şırnak", "Bartın", "Ardahan", "Iğdır", "Yalova", "Karabük", "Kilis", "Osmaniye", "Düzce",
    ];

    /// <summary>Büyük illerin ilçeleri; listede olmayan il için <see cref="GenericDistricts"/> kullanılır.</summary>
    public static readonly Dictionary<string, string[]> Districts = new(StringComparer.Ordinal)
    {
        ["İstanbul"] = ["Kadıköy", "Beşiktaş", "Üsküdar", "Şişli", "Bakırköy", "Fatih", "Beyoğlu", "Ataşehir", "Maltepe", "Kartal", "Pendik", "Sarıyer", "Esenyurt", "Başakşehir", "Beylikdüzü"],
        ["Ankara"] = ["Çankaya", "Keçiören", "Yenimahalle", "Mamak", "Etimesgut", "Sincan", "Altındağ", "Pursaklar", "Gölbaşı"],
        ["İzmir"] = ["Konak", "Karşıyaka", "Bornova", "Buca", "Çiğli", "Bayraklı", "Karabağlar", "Gaziemir", "Narlıdere", "Urla"],
        ["Bursa"] = ["Osmangazi", "Nilüfer", "Yıldırım", "İnegöl", "Gemlik", "Mudanya"],
        ["Antalya"] = ["Muratpaşa", "Konyaaltı", "Kepez", "Alanya", "Manavgat", "Kemer"],
        ["Aydın"] = ["Efeler", "Nazilli", "Söke", "Kuşadası", "Didim", "Çine", "İncirliova", "Germencik"],
        ["Adana"] = ["Seyhan", "Çukurova", "Yüreğir", "Sarıçam", "Ceyhan"],
        ["Konya"] = ["Selçuklu", "Meram", "Karatay", "Ereğli", "Akşehir"],
        ["Kocaeli"] = ["İzmit", "Gebze", "Darıca", "Körfez", "Gölcük", "Başiskele"],
        ["Eskişehir"] = ["Odunpazarı", "Tepebaşı"],
        ["Gaziantep"] = ["Şahinbey", "Şehitkamil", "Nizip"],
        ["Muğla"] = ["Menteşe", "Bodrum", "Fethiye", "Marmaris", "Milas", "Dalaman"],
        ["Denizli"] = ["Pamukkale", "Merkezefendi", "Çivril"],
        ["Trabzon"] = ["Ortahisar", "Akçaabat", "Yomra", "Of"],
        ["Samsun"] = ["İlkadım", "Atakum", "Canik", "Bafra"],
    };

    public static readonly string[] GenericDistricts = ["Merkez"];

    public static readonly string[] NeighborhoodRoots =
    [
        "Atatürk", "Cumhuriyet", "Yenimahalle", "Fatih", "Mimar Sinan", "Yıldız", "Barbaros", "Kuştepe", "Esentepe",
        "Bahçelievler", "Çamlıca", "Gazi", "Hürriyet", "İstiklal", "Kazım Karabekir", "Merkez", "Pınarbaşı", "Sakarya",
        "Ata", "Güzelyalı", "Zafer", "Emek", "Yeşilyurt", "Bağlarbaşı", "Kocatepe", "Karşıyaka", "Gülbahar", "Çiçek",
    ];

    public static readonly string[] StreetRoots =
    [
        "Gül", "Lale", "Menekşe", "Papatya", "Ihlamur", "Çınar", "Akasya", "Zeytin", "Nar", "Kiraz", "Defne", "Manolya",
        "Leylak", "Sümbül", "Nergis", "Erguvan", "Şehit Ali", "Gazi", "Kurtuluş", "İnönü", "Cumhuriyet", "Atatürk",
        "Bağdat", "Halaskargazi", "Mithatpaşa", "Kıbrıs Şehitleri", "Talatpaşa", "Ziya Gökalp", "Mevlana", "Yunus Emre",
    ];

    public static readonly string[] StreetTypes = ["Sokak", "Sokak", "Sokak", "Caddesi", "Caddesi", "Bulvarı"];

    /// <summary>Plaka kodu sırası <see cref="Cities"/> ile aynı (01 Adana ... 81 Düzce).</summary>
    public static int PlateCode(string city) => Array.IndexOf(Cities, city) + 1;

    /// <summary>Sabit hat alan kodları (il → kod); listede olmayanlar için 212.</summary>
    public static readonly Dictionary<string, string> AreaCodes = new(StringComparer.Ordinal)
    {
        ["İstanbul"] = "212", ["Ankara"] = "312", ["İzmir"] = "232", ["Bursa"] = "224", ["Antalya"] = "242",
        ["Aydın"] = "256", ["Adana"] = "322", ["Konya"] = "332", ["Kocaeli"] = "262", ["Eskişehir"] = "222",
    };

    public static readonly string[] MobilePrefixes =
    [
        "501", "505", "506", "507", "530", "531", "532", "533", "534", "535", "536", "537", "538", "539",
        "541", "542", "543", "544", "545", "546", "547", "548", "549", "551", "552", "553", "554", "555", "559",
    ];

    public static readonly string[] CompanySectors =
    [
        "Tekstil", "Gıda", "Yazılım", "Bilişim", "İnşaat", "Lojistik", "Turizm", "Otomotiv", "Elektronik", "Mobilya",
        "Tarım", "Enerji", "Danışmanlık", "Medya", "Kozmetik", "Ambalaj", "Makina", "Kimya", "Sağlık", "Eğitim",
    ];

    public static readonly string[] CompanyWords =
    [
        "Anadolu", "Ege", "Marmara", "Akdeniz", "Doğu", "Yıldız", "Güneş", "Altın", "Mavi", "Yeşil", "Kuzey", "Delta",
        "Atlas", "Zirve", "Pınar", "Çınar", "Kartal", "Lider", "Yeni", "Modern",
    ];

    public static readonly string[] CompanySuffixes = ["A.Ş.", "Ltd. Şti.", "Ltd. Şti.", "San. ve Tic. A.Ş.", "San. ve Tic. Ltd. Şti."];

    public static readonly string[] ProductAdjectives =
    [
        "Akıllı", "Pratik", "Şık", "Klasik", "Modern", "Ergonomik", "Hafif", "Dayanıklı", "Kompakt", "Lüks", "Yumuşak",
        "Su Geçirmez", "Katlanabilir", "Taşınabilir", "Kablosuz", "Organik", "Doğal", "El Yapımı", "Slim", "Oversize",
        "Vintage", "Sportif", "Premium", "Basic", "Çok Amaçlı",
    ];

    public static readonly string[] ProductMaterials =
    [
        "Pamuklu", "Keten", "Deri", "Yün", "Ahşap", "Seramik", "Cam", "Çelik", "Alüminyum", "Bambu", "Kadife", "Saten",
        "Silikon", "Porselen", "Bakır",
    ];

    public static readonly string[] ProductNouns =
    [
        "Gömlek", "Tişört", "Elbise", "Pantolon", "Mont", "Kazak", "Ayakkabı", "Çanta", "Cüzdan", "Saat", "Gözlük",
        "Kupa", "Tabak", "Tencere", "Masa", "Sandalye", "Lamba", "Yastık", "Battaniye", "Havlu", "Defter", "Kalem",
        "Kulaklık", "Klavye", "Mouse", "Hoparlör", "Powerbank", "Telefon Kılıfı", "Matara", "Termos", "Sırt Çantası",
        "Kemer", "Atkı", "Bere", "Eldiven", "Terlik", "Bot", "Şort", "Etek", "Ceket",
    ];

    public static readonly string[] Departments =
    [
        "Kadın", "Erkek", "Çocuk", "Ayakkabı", "Çanta", "Aksesuar", "Ev ve Yaşam", "Mutfak", "Elektronik", "Bilgisayar",
        "Telefon", "Kozmetik", "Spor", "Kitap", "Kırtasiye", "Oyuncak", "Bahçe", "Yapı Market", "Pet Shop", "Süpermarket",
    ];

    public static readonly string[] Colors =
    [
        "Siyah", "Beyaz", "Gri", "Lacivert", "Mavi", "Kırmızı", "Bordo", "Yeşil", "Haki", "Sarı", "Turuncu", "Pembe",
        "Mor", "Bej", "Kahverengi", "Ekru", "Antrasit", "Füme",
    ];

    public static readonly string[] ClothingSizes = ["XS", "S", "M", "L", "XL", "XXL"];

    public static readonly string[] BankCodes = ["00010", "00012", "00015", "00046", "00062", "00064", "00067", "00111", "00134", "00205"];

    public static readonly string[] Words =
    [
        "kalite", "hızlı", "güvenli", "kolay", "uygun", "yeni", "özel", "doğal", "rahat", "şık", "pratik", "güçlü",
        "tasarım", "ürün", "hizmet", "müşteri", "sipariş", "teslimat", "fiyat", "indirim", "kampanya", "stok", "kargo",
        "garanti", "iade", "destek", "deneyim", "kullanım", "günlük", "uzun", "ömürlü", "malzeme", "renk", "beden",
        "model", "koleksiyon", "sezon", "yaz", "kış", "ilkbahar", "sonbahar", "ev", "ofis", "seyahat", "spor", "çocuk",
        "aile", "hediye", "paket", "kutu", "sevgi", "mutluluk", "keyif", "zaman", "gün", "hafta", "ay", "yıl", "şehir",
        "doğa", "deniz", "orman", "dağ", "yol", "kahve", "çay", "kitap", "müzik", "film", "oyun", "sanat", "bilgi",
        "çözüm", "fikir", "plan", "proje", "ekip", "başarı", "hedef", "değer", "güven", "kalıcı", "sade", "modern",
        "klasik", "yumuşak", "hafif", "dayanıklı", "temiz", "parlak", "sıcak", "serin", "geniş", "küçük", "büyük",
        "önemli", "gerekli", "farklı", "benzersiz", "ekonomik", "verimli", "esnek", "akıllı", "estetik", "işlevsel",
    ];
}
