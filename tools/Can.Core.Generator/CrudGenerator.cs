namespace Can.Core.Generator;

/// <summary>Üretim seçenekleri.</summary>
public sealed record CrudOptions
{
    /// <summary>Çoğul ad (klasör, tablo, adres). Boşsa İngilizce kurallarla türetilir.</summary>
    public string? Plural { get; init; }
}

/// <summary>
/// Bir entity için CRUD özelliğini üretir:
/// <code>
/// Application/Features/{Çoğul}/
///   Commands/Create|Update|Delete/   {İşlem}{Entity}Command (+ Handler) ve Validator
///   Queries/GetById/                 Get{Entity}ByIdQuery
///   Queries/GetList/                 Get{Çoğul}Query (sayfalı, dinamik filtre/sıralama)
///   Dtos/                            {Entity}Dto, {Entity}ListItemDto
///   Constants/                       {Entity}Errors, {Entity}Permissions
///   Rules/                           {Entity}Rules (Ensure{Entity}ExistsAsync ...)
/// Persistence/Configurations/        {Entity}Configuration
/// WebApi/Endpoints/                  {Entity}Endpoints (/api/v1/{çoğul})
/// </code>
/// ve <c>// can-gen:...</c> işaretli yerlere DbSet, yetki, servis kaydı, uç nokta kaydı ve çeviri ekler.
/// Geliştiricinin tamamlaması gereken yerlere <c>TODO(can-gen)</c> yorumu bırakır.
/// </summary>
public static class CrudGenerator
{
    public static GenerationPlan Plan(SolutionLayout layout, EntityModel entity, CrudOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(entity);

        var n = new CrudNames(layout, entity, options?.Plural);
        var plan = new GenerationPlan();
        string feature = Path.Combine(layout.Application, "Features", n.Plural);

        plan.AddFile(Path.Combine(feature, "Constants", $"{n.Entity}Permissions.cs"), CrudTemplates.Permissions(n));
        plan.AddFile(Path.Combine(feature, "Constants", $"{n.Entity}Errors.cs"), CrudTemplates.Errors(n));
        plan.AddFile(Path.Combine(feature, "Rules", $"{n.Entity}Rules.cs"), CrudTemplates.Rules(n));
        plan.AddFile(Path.Combine(feature, "Dtos", $"{n.Entity}Dto.cs"), CrudTemplates.Dto(n));
        plan.AddFile(Path.Combine(feature, "Dtos", $"{n.Entity}ListItemDto.cs"), CrudTemplates.ListItemDto(n));
        plan.AddFile(Path.Combine(feature, "Commands", "Create", $"Create{n.Entity}Command.cs"), CrudTemplates.CreateCommand(n));
        plan.AddFile(Path.Combine(feature, "Commands", "Create", $"Create{n.Entity}CommandValidator.cs"), CrudTemplates.Validator(n, "Create", n.CreateParameters));
        plan.AddFile(Path.Combine(feature, "Commands", "Update", $"Update{n.Entity}Command.cs"), CrudTemplates.UpdateCommand(n));
        plan.AddFile(Path.Combine(feature, "Commands", "Update", $"Update{n.Entity}CommandValidator.cs"), CrudTemplates.Validator(n, "Update", n.UpdateParameters));
        plan.AddFile(Path.Combine(feature, "Commands", "Delete", $"Delete{n.Entity}Command.cs"), CrudTemplates.DeleteCommand(n));
        plan.AddFile(Path.Combine(feature, "Queries", "GetById", $"Get{n.Entity}ByIdQuery.cs"), CrudTemplates.GetByIdQuery(n));
        plan.AddFile(Path.Combine(feature, "Queries", "GetList", $"Get{n.Plural}Query.cs"), CrudTemplates.GetListQuery(n));
        plan.AddFile(Path.Combine(layout.Persistence, "Configurations", $"{n.Entity}Configuration.cs"), CrudTemplates.Configuration(n));
        plan.AddFile(Path.Combine(layout.WebApi, "Endpoints", $"{n.Entity}Endpoints.cs"), CrudTemplates.Endpoints(n));

        plan.Inject(
            MarkerFile(layout.Persistence, "can-gen:dbsets", "AppDbContext.cs"),
            "can-gen:dbsets",
            $"public DbSet<{n.Entity}> {n.Plural} => Set<{n.Entity}>();",
            entity.Namespace);

        string permissions = MarkerFile(layout.Application, "can-gen:permissions", Path.Combine("Common", "AppPermissions.cs"));
        plan.Inject(permissions, "can-gen:permissions", $"..{n.Entity}Permissions.All,", n.ConstantsNamespace);
        plan.Inject(permissions, "can-gen:user-grants", $"// {n.Entity}Permissions.Read, // TODO(can-gen): User rolü {n.Words} kayıtlarını görebilsin mi?");

        plan.Inject(
            MarkerFile(layout.Application, "can-gen:services", "DependencyInjection.cs"),
            "can-gen:services",
            $"services.AddScoped<{n.Entity}Rules>();",
            n.RulesNamespace);

        plan.Inject(
            MarkerFile(layout.WebApi, "can-gen:endpoints", Path.Combine("Endpoints", "EndpointRegistration.cs")),
            "can-gen:endpoints",
            $"app.Map{n.Entity}Endpoints();     // /api/v1/{n.Route}");

        string localization = Path.Combine(layout.WebApi, "Localization");
        if (Directory.Exists(localization))
        {
            foreach (string file in Directory.EnumerateFiles(localization, "*.json"))
            {
                bool turkish = Path.GetFileNameWithoutExtension(file).StartsWith("tr", StringComparison.OrdinalIgnoreCase);
                string message = turkish ? $"{n.Words} kaydı bulunamadı." : $"{n.Words} not found.";
                plan.Edit(file, $"\"{n.ErrorPrefix}.not_found\" çevirisi", text => SourceEditor.AddJsonGroup(text, n.ErrorPrefix, "not_found", message));
            }
        }

        return plan;
    }

    /// <summary>İşareti içeren dosyayı katmanda arar; bulunamazsa beklenen yolu döner (uygulamada "işaret yok" uyarısı verilir).</summary>
    private static string MarkerFile(string directory, string marker, string expected)
    {
        string text = "// " + marker;
        return SolutionLayout.SourceFiles(directory).FirstOrDefault(f => File.ReadLines(f).Any(l => l.Trim() == text))
            ?? Path.Combine(directory, expected);
    }
}

/// <summary>Bir entity için üretilen tüm adlar.</summary>
internal sealed class CrudNames
{
    public CrudNames(SolutionLayout layout, EntityModel entity, string? plural)
    {
        Model = entity;
        Root = layout.RootNamespace;
        Entity = entity.Name;
        Plural = string.IsNullOrWhiteSpace(plural) ? Names.Plural(entity.Name) : Names.Pascal(plural.Trim());
        Words = Names.Words(entity.Name);
        Route = Names.Kebab(Plural);
        ErrorPrefix = Names.Snake(entity.Name);
        Variable = Names.Camel(entity.Name) is var v && v is "event" or "class" or "object" or "string" or "operator" ? "@" + v : Names.Camel(entity.Name);
        Repository = "_" + Names.Camel(Plural);

        FeatureNamespace = $"{Root}.Application.Features.{Plural}";

        List<MethodParameter> scalars = entity.ScalarProperties
            .Where(p => p.Name != "Id")
            .Select(p => new MethodParameter(p.Name, p.Type))
            .ToList();

        CreateParameters = entity.Factory?.Parameters.Select(Pascalize).ToList() ?? scalars;
        UpdateParameters = entity.Update?.Parameters.Select(Pascalize).ToList() ?? scalars;
    }

    public EntityModel Model { get; }

    public string Root { get; }

    public string Entity { get; }

    public string Plural { get; }

    /// <summary>"Product category" (metinler).</summary>
    public string Words { get; }

    /// <summary>"product-categories" (adres, yetki).</summary>
    public string Route { get; }

    /// <summary>"product_category" (hata kodu öneki).</summary>
    public string ErrorPrefix { get; }

    /// <summary>"brand" (yerel değişken).</summary>
    public string Variable { get; }

    /// <summary>"_brands" (repository alanı).</summary>
    public string Repository { get; }

    public string IdType => Model.IdType;

    public string FeatureNamespace { get; }

    public string ConstantsNamespace => FeatureNamespace + ".Constants";

    public string RulesNamespace => FeatureNamespace + ".Rules";

    public string DtosNamespace => FeatureNamespace + ".Dtos";

    /// <summary>Oluşturma komutunun alanları: fabrika metodunun parametreleri, yoksa entity'nin düz özellikleri.</summary>
    public IReadOnlyList<MethodParameter> CreateParameters { get; }

    /// <summary>Güncelleme komutunun alanları (Id hariç): Update metodunun parametreleri, yoksa düz özellikler.</summary>
    public IReadOnlyList<MethodParameter> UpdateParameters { get; }

    /// <summary>Adres kısıtı: <c>{id:guid}</c>.</summary>
    public string RouteId => Model.IdType switch
    {
        "Guid" => "{id:guid}",
        "int" or "Int32" => "{id:int}",
        "long" or "Int64" => "{id:long}",
        _ => "{id}",
    };

    private static MethodParameter Pascalize(MethodParameter parameter) => parameter with { Name = Names.Pascal(parameter.Name) };
}
