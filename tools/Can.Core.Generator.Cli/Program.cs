using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Can.Core.Generator;
using Can.Core.Generator.Cli;
using static Can.Core.Generator.Cli.Prompts;

// can-gen: Can.Core çözümleri için kod üretici (nArchitecture'daki nArchGen'in karşılığı). Ayrıntı: can-gen --help

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
Arguments arguments;

try
{
    arguments = Arguments.Parse(args);
    if (arguments.Json)
        Prompts.Interactive = false;

    string? command = arguments.Positional(0);
    if (arguments.Flag("version"))
        command = "version";
    if (arguments.Flag("help") && command is not "generate")
        command = "help";

    return command switch
    {
        "new" => New(arguments),
        "generate" or "g" => Generate(arguments),
        "crud" => Crud(arguments, entityIndex: 1),
        "entities" => Entities(arguments),
        "features" => Features(arguments),
        "version" => Print(Version()),
        "help" or null => Print(Help()),
        _ => Fail($"Bilinmeyen komut: {command}\n\n{Help()}", arguments.Json),
    };
}
catch (GeneratorException exception)
{
    return Fail(exception.Message, args.Contains("--json"));
}

// ================================================================ new

int New(Arguments a)
{
    Banner();
    string name = a.Positional(1) ?? Text($"Proje {G("adı")}");
    if (string.IsNullOrWhiteSpace(name))
        throw new GeneratorException("Proje adı gerekli: can-gen new MyShop");
    if (char.IsLower(name[0]))
    {
        // Namespace'ler C# geleneğine uysun: "test3" → "Test3" (proje ve klasör adı da buna göre).
        name = char.ToUpperInvariant(name[0]) + name[1..];
        Console.WriteLine(Dim($"Proje adı {name} olarak kullanılıyor."));
    }

    var templateArgs = new List<string> { "new", "can-monolith", "-n", name, "-o", a.Option("output") ?? name };

    if (a.PassThrough.Count == 0 && Interactive)
    {
        string idKey = Select($"Kimlik {G("anahtar tipi")} (kullanıcı, rol, yetki)?", ["guid", "int", "long"]);
        templateArgs.AddRange(["--IdKeyType", idKey]);

        (string Symbol, string Label, bool Default)[] options =
        [
            ("Otp", "Security: OTP (authenticator)", true),
            ("EmailTwoFactor", "Security: 2FA (e-posta kodu)", true),
            ("Passkey", "Security: Passkey", true),
            ("ExternalLogin", "Security: Dış hesapla giriş (Google/Microsoft/GitHub)", false),
            ("MultiTenancy", "Tenancy: Multi-tenancy", false),
            ("Redis", "Altyapı: Redis", false),
            ("EventBus", "Altyapı: Event bus + outbox", false),
            ("Hangfire", "Altyapı: Hangfire", false),
            ("FileStorage", "Altyapı: Dosya depolama", false),
            ("Search", "Altyapı: Arama", false),
            ("Realtime", "Altyapı: SignalR", false),
            ("Sms", "Altyapı: SMS", false),
            ("AuditTrail", "Modül: Değişiklik geçmişi", true),
            ("Settings", "Modül: Ayarlar", false),
            ("Reporting", "Modül: Raporlama", false),
            ("OpenTelemetry", "Modül: OpenTelemetry", false),
        ];
        IReadOnlyList<string> chosen = MultiSelect($"Hangi {G("özellikler")}?", options.Select(o => (o.Label, o.Default)).ToList());
        foreach ((string symbol, string label, _) in options)
            templateArgs.AddRange([$"--{symbol}", chosen.Contains(label) ? "true" : "false"]);
    }

    templateArgs.AddRange(a.PassThrough);

    string? feed = a.Option("feed") ?? Environment.GetEnvironmentVariable("CANCORE_FEED");
    if (feed is null && Interactive)
        feed = Text($"Can.Core {G("paket kaynağı")} (klasör ya da NuGet adresi; boş: sonra nuget.config'e yaz)");
    if (!string.IsNullOrWhiteSpace(feed))
        templateArgs.AddRange(["--CanCoreFeed", feed]);

    Console.WriteLine(Dim("dotnet " + string.Join(' ', templateArgs)));
    using Process process = Process.Start(new ProcessStartInfo("dotnet", templateArgs) { UseShellExecute = false })
        ?? throw new GeneratorException("dotnet çalıştırılamadı.");
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new GeneratorException("Şablon çalışmadı. Şablon kurulu mu? (dotnet new list can-monolith)");

    Console.WriteLine($"\n✅ {B(name)} oluşturuldu. Sıradaki adımlar:");
    Console.WriteLine($"   cd {a.Option("output") ?? name}");
    Console.WriteLine("   # entity'yi src/*.Domain/Entities altına yaz, sonra:");
    Console.WriteLine("   can-gen generate crud <Entity>");
    return 0;
}

// ================================================================ generate

int Generate(Arguments a)
{
    string? what = a.Positional(1);
    if (what is null || a.Flag("help"))
        return Print(GenerateHelp());

    return what switch
    {
        "crud" => Crud(a, entityIndex: 2),
        "command" => Request(a, isCommand: true),
        "query" => Request(a, isCommand: false),
        "dynamic-query" => DynamicQuery(a),
        _ => Fail($"Bilinmeyen üretim: {what}\n\n{GenerateHelp()}", a.Json),
    };
}

int Crud(Arguments a, int entityIndex)
{
    Banner();
    (SolutionLayout layout, EntityModel entity) = SelectEntity(a, entityIndex);

    (bool caching, bool logging, bool transaction, bool secured, bool dynamic) = Mechanisms(a, includeTransaction: true, includeDynamic: true);
    var options = new CrudOptions
    {
        Plural = a.Option("plural"),
        Caching = caching,
        Logging = logging,
        Transaction = transaction,
        Secured = secured,
        DynamicQuery = dynamic,
    };

    IReadOnlyList<ChangeResult> results = CrudGenerator.Plan(layout, entity, options).Apply(a.Flag("force"), a.Flag("dry-run"));
    bool createTodo = entity.Factory is null && !entity.HasPublicConstructor;
    bool updateTodo = entity.Update is null && !entity.ScalarProperties.Where(p => p.Name != "Id").All(p => p.HasPublicSetter);

    var warnings = new List<string>();
    if (createTodo)
        warnings.Add($"{entity.Name}'de 'Create(...)' fabrika metodu ya da public constructor yok; oluşturma handler'ında TODO bırakıldı.");
    if (updateTodo)
        warnings.Add($"{entity.Name}'de 'Update(...)' metodu ya da public setter'lar yok; güncelleme handler'ında TODO bırakıldı.");

    return Report(a, layout, results, $"{entity.Name} ({entity.Namespace}, Id: {entity.IdType})", warnings, new
    {
        entity.Name,
        entity.Namespace,
        entity.IdType,
        entity.FilePath,
        HasFactory = !createTodo,
        HasUpdate = !updateTodo,
        CreateTodo = createTodo,
        UpdateTodo = updateTodo,
    });
}

int DynamicQuery(Arguments a)
{
    Banner();
    (SolutionLayout layout, EntityModel entity) = SelectEntity(a, 2);
    IReadOnlyList<ChangeResult> results = CrudGenerator
        .PlanDynamicQuery(layout, entity, new CrudOptions { Plural = a.Option("plural"), Secured = !a.Flag("no-secured") })
        .Apply(a.Flag("force"), a.Flag("dry-run"));
    return Report(a, layout, results, $"{entity.Name}: GetListByDynamic", [], new { entity.Name });
}

int Request(Arguments a, bool isCommand)
{
    Banner();
    SolutionLayout layout = Layout(a);
    string kind = isCommand ? "komut" : "sorgu";

    string name = a.Positional(2) ?? Text($"Yeni {G(kind)} adı");
    if (string.IsNullOrWhiteSpace(name))
        throw new GeneratorException($"{kind} adı gerekli: can-gen generate {(isCommand ? "command SyncUser Users" : "query GetUserByEmail Users")}");

    string feature = a.Positional(3) ?? Select($"{Names.Pascal(kind)} hangi {G("özellikte")}?", FeatureGenerator.ListFeatures(layout));

    (bool caching, bool logging, bool transaction, bool secured, _) = Mechanisms(a, includeTransaction: isCommand, includeDynamic: false);
    var options = new RequestOptions
    {
        Caching = caching,
        Logging = logging,
        Transaction = transaction,
        Secured = secured,
        EndpointMethod = a.Option("endpoint-method"),
    };

    GenerationPlan plan = isCommand
        ? FeatureGenerator.PlanCommand(layout, feature, name, options)
        : FeatureGenerator.PlanQuery(layout, feature, name, options);
    IReadOnlyList<ChangeResult> results = plan.Apply(a.Flag("force"), a.Flag("dry-run"));
    return Report(a, layout, results, $"{feature}: {Names.Pascal(name)} ({kind})", [], new { Feature = feature, Name = name });
}

// ================================================================ listeler

int Entities(Arguments a)
{
    SolutionLayout layout = Layout(a);
    IReadOnlyList<(string Name, string FilePath)> entities = EntityReader.List(layout);
    if (a.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, solution = layout.Root, entities = entities.Select(e => new { e.Name, e.FilePath }) }, json));
        return 0;
    }

    foreach ((string name, string path) in entities)
        Console.WriteLine($"{name,-30} {Dim(Path.GetRelativePath(layout.Root, path))}");
    return 0;
}

int Features(Arguments a)
{
    SolutionLayout layout = Layout(a);
    IReadOnlyList<string> features = FeatureGenerator.ListFeatures(layout);
    if (a.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, solution = layout.Root, features }, json));
        return 0;
    }

    foreach (string feature in features)
        Console.WriteLine(feature);
    return 0;
}

// ================================================================ yardımcılar

SolutionLayout Layout(Arguments a, string? hint = null)
{
    SolutionLayout layout = SolutionLayout.Find(a.Option("project") ?? hint ?? Directory.GetCurrentDirectory());
    if (Interactive && !a.Json)
        Console.WriteLine($"Seçili {G("çözüm")}: {B(Path.GetFileName(layout.Root))}.");
    return layout;
}

(SolutionLayout Layout, EntityModel Entity) SelectEntity(Arguments a, int index)
{
    string? target = a.Positional(index);
    SolutionLayout layout = Layout(a, target is not null && target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? target : null);
    if (target is null)
    {
        IReadOnlyList<(string Name, string FilePath)> entities = EntityReader.List(layout).Where(e => e.Name != "AppUser").ToList();
        if (entities.Count == 0)
            throw new GeneratorException($"Domain'de entity yok ({layout.Domain}). Entity'yi Entities klasörüne yazıp tekrar dene.");
        target = Select($"Hangi {G("entity")}?", entities.Select(e => e.Name).ToList());
    }

    return (layout, EntityReader.Read(layout, target));
}

/// <summary>
/// Mekanizmalar: bayrak verilmişse (-c -l -t -s -d) yalnızca onlar; verilmemişse etkileşimli seçim (hepsi işaretli),
/// etkileşimsiz çalışmada hepsi açık.
/// </summary>
(bool Caching, bool Logging, bool Transaction, bool Secured, bool Dynamic) Mechanisms(Arguments a, bool includeTransaction, bool includeDynamic)
{
    string[] flags = ["caching", "logging", "transaction", "secured", "dynamic-query", "no-mechanisms"];
    if (flags.Any(a.Flag))
        return (a.Flag("caching"), a.Flag("logging"), a.Flag("transaction"), a.Flag("secured"), a.Flag("dynamic-query"));

    if (!Interactive)
        return (true, true, true, true, true);

    var options = new List<(string, bool)> { ("Caching", true), ("Logging", true) };
    if (includeTransaction)
        options.Add(("Transaction", true));
    options.Add(("Secured Operation", true));
    if (includeDynamic)
        options.Add(("Dynamic Query", true));

    IReadOnlyList<string> chosen = MultiSelect($"Hangi {G("mekanizmalar")} kullanılsın?", options);
    return (chosen.Contains("Caching"), chosen.Contains("Logging"), chosen.Contains("Transaction"), chosen.Contains("Secured Operation"), chosen.Contains("Dynamic Query"));
}

int Report(Arguments a, SolutionLayout layout, IReadOnlyList<ChangeResult> results, string title, IReadOnlyList<string> warnings, object details)
{
    bool failed = results.Any(r => r.Kind == ChangeKind.Failed);
    if (a.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = !failed, solution = layout.Root, entity = details, dryRun = a.Flag("dry-run"), warnings, changes = results }, json));
        return failed ? 1 : 0;
    }

    Console.WriteLine($"{title}{(a.Flag("dry-run") ? Warn(" — deneme, diske yazılmadı") : string.Empty)}");

    void Section(string header, Func<ChangeResult, bool> filter, string mark)
    {
        List<ChangeResult> items = results.Where(filter).ToList();
        if (items.Count == 0)
            return;
        Console.WriteLine($"{mark} {G(header)}");
        foreach (ChangeResult item in items)
            Console.WriteLine($"   {B(Path.GetRelativePath(layout.Root, item.Path))}{(item.Note is null ? string.Empty : Dim($"  ({item.Note})"))}");
    }

    Section("Üretilen dosyalar:", r => r.Kind is ChangeKind.Created or ChangeKind.Overwritten, "🆕");
    Section("Güncellenen dosyalar:", r => r.Kind == ChangeKind.Updated, "🔼");
    Section("Atlanan dosyalar (zaten var, --force ile üzerine yazılır):", r => r.Kind == ChangeKind.Skipped, "⏭ ");
    Section("Başarısız:", r => r.Kind == ChangeKind.Failed, "❌");

    foreach (string warning in warnings)
        Console.WriteLine(Warn("⚠ " + warning));

    Console.WriteLine(Dim("Tamamlanacak yerler 'TODO(can-gen)' ile işaretli (Rider: View → Tool Windows → TODO)."));
    return failed ? 1 : 0;
}

int Fail(string message, bool asJson)
{
    if (asJson)
        Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message }, json));
    else
        Console.Error.WriteLine("Hata: " + message);
    return 2;
}

static int Print(string text)
{
    Console.WriteLine(text);
    return 0;
}

static string Version() =>
    typeof(CrudGenerator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

static string Help() => """
    can-gen — Can.Core kod üretici (nArchitecture düzeni)

    ÖRNEKLER:
        can-gen new MyShop
        can-gen generate crud Brand
        can-gen generate command SyncBrand Brands
        can-gen generate query GetBrandByName Brands
        can-gen generate dynamic-query Brand

    KOMUTLAR:
        new [Ad]                  Can.Core şablonuyla yeni çözüm (veritabanı ve özellikler sorulur)
        generate (g)              Kod üret: crud, command, query, dynamic-query (can-gen generate --help)
        entities                  Domain'deki entity'leri listeler
        features                  Application/Features altındaki özellikleri listeler
        version                   Sürümü yazar

    SEÇENEKLER:
        -p, --project <klasör>    Çözüm klasörü (varsayılan: bulunulan klasör)
        -f, --force               Var olan dosyaların üzerine yaz
            --dry-run             Yalnızca ne yapılacağını göster
            --json                Makine okunur çıktı, soru sorulmaz (IDE eklentileri için)
        -h, --help                Yardım
        -v, --version             Sürüm
    """;

static string GenerateHelp() => """
    can-gen generate — kod üret

    ÖRNEKLER:
        can-gen generate crud                         (entity ve mekanizmalar sorulur)
        can-gen generate crud Brand -c -l -t -s -d
        can-gen generate crud src/Shop.Domain/Entities/Brand.cs --plural Brands
        can-gen generate command SyncBrand Brands -e Put
        can-gen generate query GetBrandByName Brands -c -s
        can-gen generate dynamic-query Brand

    ÜRETİMLER:
        crud [Entity]                     Create/Update/Delete komutları, GetById/GetList(+ByDynamic) sorguları, yanıtlar,
                                          Messages/OperationClaims, MappingProfiles, BusinessRules, repository, servis
                                          (IXService/XManager), EF yapılandırması, controller; DbSet, kayıtlar, yetkiler, çeviri
        command [Ad] [Özellik]            Özelliğe komut (+ yanıt, validator, yetki, controller ucu)
        query [Ad] [Özellik]              Özelliğe sorgu (+ yanıt, validator, yetki, controller ucu)
        dynamic-query [Entity]            CRUD'a sonradan dinamik filtre/sıralama sorgusu

    MEKANİZMALAR (hiçbiri verilmezse sorulur; etkileşimsizde hepsi açık):
        -c, --caching                     Sorgular önbelleğe alınır (kayıt değişince kendiliğinden silinir)
        -l, --logging                     Komutlar loglanır
        -t, --transaction                 Komutlar transaction içinde (kapalıysa handler kendisi kaydeder)
        -s, --secured                     Yetki kontrolü (OperationClaims)
        -d, --dynamic-query               GetListByDynamic (yalnızca crud)

    DİĞER:
        -e, --endpoint-method <Metot>     Controller ucu: Get, Post, Put, Delete (komut: Post, sorgu: Get)
            --plural <Çoğul>              Çoğul ad (varsayılan: İngilizce kurallar)
        -p, --project <klasör>  -f, --force  --dry-run  --json
    """;
