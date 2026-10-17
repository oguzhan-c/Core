using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Can.Core.Generator;

// can-gen: Can.Core çözümleri için kod üretici.
//   can-gen crud <Entity | Entity.cs> [--project <klasör>] [--plural <Çoğul>] [--force] [--dry-run] [--json]
//   can-gen entities [--project <klasör>] [--json]

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

try
{
    var arguments = Arguments.Parse(args);
    return arguments.Command switch
    {
        "crud" => Crud(arguments),
        "entities" => Entities(arguments),
        "version" or "--version" => Print(Version()),
        "help" or "--help" or "-h" or null => Print(Help()),
        _ => Fail($"Bilinmeyen komut: {arguments.Command}\n\n{Help()}", arguments.Json),
    };
}
catch (GeneratorException exception)
{
    return Fail(exception.Message, args.Contains("--json"));
}

int Crud(Arguments arguments)
{
    string target = arguments.Positional ?? throw new GeneratorException("Entity adı ya da dosyası gerekli: can-gen crud Brand");
    string start = arguments.Option("project") ?? (target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? target : Directory.GetCurrentDirectory());

    SolutionLayout layout = SolutionLayout.Find(start);
    EntityModel entity = EntityReader.Read(layout, target);
    GenerationPlan plan = CrudGenerator.Plan(layout, entity, new CrudOptions { Plural = arguments.Option("plural") });
    IReadOnlyList<ChangeResult> results = plan.Apply(arguments.Flag("force"), arguments.Flag("dry-run"));

    bool failed = results.Any(r => r.Kind == ChangeKind.Failed);
    bool createTodo = entity.Factory is null && !entity.HasPublicConstructor;
    bool updateTodo = entity.Update is null && !entity.ScalarProperties.Where(p => p.Name != "Id").All(p => p.HasPublicSetter);
    if (arguments.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = !failed,
            solution = layout.Root,
            entity = new { entity.Name, entity.Namespace, entity.IdType, entity.FilePath, HasFactory = !createTodo, HasUpdate = !updateTodo, CreateTodo = createTodo, UpdateTodo = updateTodo },
            dryRun = arguments.Flag("dry-run"),
            changes = results,
        }, json));
        return failed ? 1 : 0;
    }

    Console.WriteLine($"{entity.Name} ({entity.Namespace}, Id: {entity.IdType}){(arguments.Flag("dry-run") ? " — deneme, diske yazılmadı" : string.Empty)}");
    foreach (ChangeResult result in results)
    {
        string mark = result.Kind switch
        {
            ChangeKind.Created => "+",
            ChangeKind.Overwritten => "!",
            ChangeKind.Updated => "~",
            ChangeKind.Unchanged => "=",
            ChangeKind.Skipped => "-",
            _ => "x",
        };
        Console.WriteLine($"  {mark} {Path.GetRelativePath(layout.Root, result.Path)}{(result.Note is null ? string.Empty : $"  ({result.Note})")}");
    }

    if (createTodo)
        Console.WriteLine($"\nUyarı: {entity.Name}'de 'Create(...)' fabrika metodu ya da public constructor yok; oluşturma handler'ında TODO bırakıldı.");
    if (updateTodo)
        Console.WriteLine($"Uyarı: {entity.Name}'de 'Update(...)' metodu ya da public setter'lar yok; güncelleme handler'ında TODO bırakıldı.");

    Console.WriteLine("\nTamamlanacak yerler 'TODO(can-gen)' ile işaretli (Rider: View → Tool Windows → TODO).");
    return failed ? 1 : 0;
}

int Entities(Arguments arguments)
{
    SolutionLayout layout = SolutionLayout.Find(arguments.Option("project") ?? Directory.GetCurrentDirectory());
    IReadOnlyList<(string Name, string FilePath)> entities = EntityReader.List(layout);

    if (arguments.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, solution = layout.Root, entities = entities.Select(e => new { e.Name, e.FilePath }) }, json));
        return 0;
    }

    foreach ((string name, string path) in entities)
        Console.WriteLine($"{name,-30} {Path.GetRelativePath(layout.Root, path)}");
    return 0;
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
    typeof(CrudGenerator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

static string Help() => """
    can-gen — Can.Core kod üretici

    Komutlar:
      crud <Entity | Entity.cs>   Entity için nArchitecture düzeninde CRUD üretir: Create/Update/Delete komutları ve
                                  yanıtları, GetById/GetList/GetListByDynamic sorguları, Messages/OperationClaims,
                                  MappingProfiles, BusinessRules, repository, EF yapılandırması, controller.
      entities                    Domain'deki entity'leri listeler.
      version                     Sürümü yazar.

    Seçenekler:
      --project <klasör>          Çözüm klasörü (varsayılan: bulunulan klasör ya da entity dosyasının klasörü).
      --plural <Çoğul>            Çoğul ad (varsayılan: İngilizce kurallar; Category → Categories).
      --force                     Var olan dosyaların üzerine yaz.
      --dry-run                   Yalnızca ne yapılacağını göster.
      --json                      Makine okunur çıktı (IDE eklentileri için).

    Örnek:
      can-gen crud Brand
      can-gen crud src/MyApp.Domain/Catalog/Brand.cs --dry-run
    """;

/// <summary>Basit argüman ayrıştırıcı (dış paket kullanılmaz).</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private set; }

    public string? Positional { get; private set; }

    public bool Json => Flag("json");

    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2 && result.Command is not null)
            {
                string name = arg[2..];
                bool hasValue = name is "project" or "plural";
                if (hasValue && i + 1 >= args.Length)
                    throw new GeneratorException($"--{name} için değer gerekli.");
                result._options[name] = hasValue ? args[++i] : null;
            }
            else if (result.Command is null)
            {
                result.Command = arg;
            }
            else
            {
                result.Positional ??= arg;
            }
        }

        return result;
    }

    public bool Flag(string name) => _options.ContainsKey(name);

    public string? Option(string name) => _options.TryGetValue(name, out string? value) ? value : null;
}
