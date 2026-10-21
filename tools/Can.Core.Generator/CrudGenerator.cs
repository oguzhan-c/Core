namespace Can.Core.Generator;

/// <summary>Üretim seçenekleri.</summary>
public sealed record CrudOptions
{
    /// <summary>Çoğul ad (klasör, sınıf, tablo, adres). Boşsa İngilizce kurallarla türetilir.</summary>
    public string? Plural { get; init; }

    /// <summary>Sorgular önbelleğe alınır (<c>ICachableRequest</c>); kayıt değişince önbellek kendiliğinden silinir.</summary>
    public bool Caching { get; init; } = true;

    /// <summary>Komutlar loglanır (<c>ILoggableRequest</c>).</summary>
    public bool Logging { get; init; } = true;

    /// <summary>Komutlar transaction içinde çalışır ve sonunda kaydedilir (<c>ITransactionalRequest</c>). Kapalıysa handler kendisi kaydeder.</summary>
    public bool Transaction { get; init; } = true;

    /// <summary>İstekler yetki ister (<c>ISecuredRequest</c> + OperationClaims).</summary>
    public bool Secured { get; init; } = true;

    /// <summary>Dinamik filtre/sıralama sorgusu (GetListByDynamic) üretilir.</summary>
    public bool DynamicQuery { get; init; } = true;
}

/// <summary>
/// Bir entity için CRUD özelliğini nArchitecture düzeninde üretir:
/// <code>
/// Application/Features/{Çoğul}/
///   Commands/Create|Update|Delete/   {İşlem}{Entity}Command (+ Handler), Validator, {Created|Updated|Deleted}{Entity}Response
///   Queries/GetById/                 GetById{Entity}Query, GetById{Entity}Response
///   Queries/GetList/                 GetList{Entity}Query, GetList{Entity}ListItemDto
///   Queries/GetListByDynamic/        GetListByDynamic{Entity}Query
///   Constants/                       {Çoğul}Messages, {Çoğul}OperationClaims
///   Profiles/                        MappingProfiles
///   Rules/                           {Entity}BusinessRules
/// Application/Services/Repositories/ I{Entity}Repository
/// Persistence/Repositories/          {Entity}Repository
/// Persistence/EntityConfigurations/  {Entity}Configuration
/// WebAPI/Controllers/                {Çoğul}Controller (/api/v1/{çoğul})
/// </code>
/// ve <c>// can-gen:...</c> işaretli yerlere DbSet, yetki, repository kaydı ve çeviri ekler. Geliştiricinin tamamlaması
/// gereken yerlere <c>TODO(can-gen)</c> yorumu bırakır.
/// </summary>
public static class CrudGenerator
{
    public static GenerationPlan Plan(SolutionLayout layout, EntityModel entity, CrudOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(entity);

        options ??= new CrudOptions();
        var n = new CrudNames(layout, entity, options.Plural) { Options = options };
        var plan = new GenerationPlan();
        string feature = Path.Combine(layout.Application, "Features", n.Plural);
        string E = n.Entity;

        plan.AddFile(Path.Combine(feature, "Constants", $"{n.Plural}BusinessMessages.cs"), CrudTemplates.Messages(n));
        plan.AddFile(Path.Combine(feature, "Constants", $"{n.Plural}OperationClaims.cs"), CrudTemplates.OperationClaims(n));
        plan.AddFile(Path.Combine(feature, "Rules", $"{E}BusinessRules.cs"), CrudTemplates.BusinessRules(n));
        plan.AddFile(Path.Combine(feature, "Profiles", "MappingProfiles.cs"), CrudTemplates.MappingProfiles(n));

        plan.AddFile(Path.Combine(feature, "Commands", "Create", $"Create{E}Command.cs"), CrudTemplates.CreateCommand(n));
        plan.AddFile(Path.Combine(feature, "Commands", "Create", $"Create{E}CommandValidator.cs"), CrudTemplates.Validator(n, "Create", n.CreateFields));
        plan.AddFile(Path.Combine(feature, "Commands", "Create", $"Created{E}Response.cs"), CrudTemplates.Response(n, "Commands.Create", $"Created{E}Response", n.ResponseFields));
        plan.AddFile(Path.Combine(feature, "Commands", "Update", $"Update{E}Command.cs"), CrudTemplates.UpdateCommand(n));
        plan.AddFile(Path.Combine(feature, "Commands", "Update", $"Update{E}CommandValidator.cs"), CrudTemplates.Validator(n, "Update", n.UpdateFields));
        plan.AddFile(Path.Combine(feature, "Commands", "Update", $"Updated{E}Response.cs"), CrudTemplates.Response(n, "Commands.Update", $"Updated{E}Response", n.ResponseFields));
        plan.AddFile(Path.Combine(feature, "Commands", "Delete", $"Delete{E}Command.cs"), CrudTemplates.DeleteCommand(n));
        plan.AddFile(Path.Combine(feature, "Commands", "Delete", $"Delete{E}CommandValidator.cs"), CrudTemplates.DeleteValidator(n));
        plan.AddFile(Path.Combine(feature, "Commands", "Delete", $"Deleted{E}Response.cs"), CrudTemplates.Response(n, "Commands.Delete", $"Deleted{E}Response", [new("Id", n.IdType)]));

        plan.AddFile(Path.Combine(feature, "Queries", "GetById", $"GetById{E}Query.cs"), CrudTemplates.GetByIdQuery(n));
        plan.AddFile(Path.Combine(feature, "Queries", "GetById", $"GetById{E}Response.cs"), CrudTemplates.Response(n, "Queries.GetById", $"GetById{E}Response", n.ResponseFields));
        plan.AddFile(Path.Combine(feature, "Queries", "GetList", $"GetList{E}Query.cs"), CrudTemplates.GetListQuery(n));
        plan.AddFile(Path.Combine(feature, "Queries", "GetList", $"GetList{E}QueryValidator.cs"), CrudTemplates.PageQueryValidator(n, "GetList"));
        plan.AddFile(Path.Combine(feature, "Queries", "GetList", $"GetList{E}ListItemDto.cs"), CrudTemplates.Response(n, "Queries.GetList", $"GetList{E}ListItemDto", n.ResponseFields, isDto: true));
        if (options.DynamicQuery)
            AddDynamicQueryFiles(plan, n, feature);

        plan.AddFile(Path.Combine(layout.Application, "Services", "Repositories", $"I{E}Repository.cs"), CrudTemplates.RepositoryInterface(n));
        plan.AddFile(Path.Combine(layout.Application, "Services", n.Plural, $"I{E}Service.cs"), CrudTemplates.ServiceInterface(n));
        plan.AddFile(Path.Combine(layout.Application, "Services", n.Plural, $"{E}Manager.cs"), CrudTemplates.Manager(n));
        plan.AddFile(Path.Combine(layout.Persistence, "Repositories", $"{E}Repository.cs"), CrudTemplates.Repository(n));
        plan.AddFile(Path.Combine(layout.Persistence, "EntityConfigurations", $"{E}Configuration.cs"), CrudTemplates.Configuration(n));
        plan.AddFile(Path.Combine(layout.WebApi, "Controllers", $"{n.Plural}Controller.cs"), CrudTemplates.Controller(n));

        plan.Inject(
            MarkerFile(layout.Persistence, "can-gen:dbsets", Path.Combine("Contexts", "BaseDbContext.cs")),
            "can-gen:dbsets",
            $"public DbSet<{E}> {n.Plural} => Set<{E}>();",
            entity.Namespace);

        plan.Inject(
            MarkerFile(layout.Persistence, "can-gen:repositories", "PersistenceServiceRegistration.cs"),
            "can-gen:repositories",
            $"services.AddScoped<I{E}Repository, {E}Repository>();",
            $"{n.Root}.Application.Services.Repositories",
            $"{n.Root}.Persistence.Repositories");

        plan.Inject(
            MarkerFile(layout.Application, "can-gen:services", "ApplicationServiceRegistration.cs"),
            "can-gen:services",
            $"services.AddScoped<I{E}Service, {E}Manager>();",
            $"{n.Root}.Application.Services.{n.Plural}");

        string claims = MarkerFile(layout.Application, "can-gen:permissions", Path.Combine("Services", "OperationClaims", "AppOperationClaims.cs"));
        plan.Inject(claims, "can-gen:permissions", $"..{n.Plural}OperationClaims.All,", n.ConstantsNamespace);
        plan.Inject(claims, "can-gen:user-grants", $"// {n.Plural}OperationClaims.Read, // TODO(can-gen): User rolü {n.Words} kayıtlarını görebilsin mi?");

        string localization = Path.Combine(layout.WebApi, "Localization");
        if (Directory.Exists(localization))
        {
            foreach (string file in Directory.EnumerateFiles(localization, "*.json"))
            {
                bool turkish = Path.GetFileNameWithoutExtension(file).StartsWith("tr", StringComparison.OrdinalIgnoreCase);
                string message = turkish ? $"{n.Words} kaydı bulunamadı." : $"{n.Words} not found.";
                plan.Edit(file, $"\"{n.ErrorPrefix}.not_exists\" çevirisi", text => SourceEditor.AddJsonGroup(text, n.ErrorPrefix, "not_exists", message));
            }
        }

        return plan;
    }

    /// <summary>
    /// Yalnızca dinamik sorguyu üretir (CRUD daha önce <c>DynamicQuery</c> kapalıyken üretildiyse): sorgu, liste DTO'su,
    /// eşleme ve controller ucu.
    /// </summary>
    public static GenerationPlan PlanDynamicQuery(SolutionLayout layout, EntityModel entity, CrudOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(entity);

        options ??= new CrudOptions();
        var n = new CrudNames(layout, entity, options.Plural) { Options = options };
        var plan = new GenerationPlan();
        string feature = Path.Combine(layout.Application, "Features", n.Plural);
        if (!Directory.Exists(feature))
            throw new GeneratorException($"'{n.Plural}' özelliği yok ({feature}). Önce CRUD üret: can-gen generate crud {entity.Name}");

        AddDynamicQueryFiles(plan, n, feature);
        plan.Inject(
            Path.Combine(feature, "Profiles", "MappingProfiles.cs"),
            "can-gen:mappings",
            $"CreateMap<{entity.Name}, GetListByDynamic{entity.Name}ListItemDto>();",
            $"{n.FeatureNamespace}.Queries.GetListByDynamic");
        plan.Inject(
            Path.Combine(layout.WebApi, "Controllers", $"{n.Plural}Controller.cs"),
            "can-gen:endpoints",
            CrudTemplates.DynamicQueryEndpoint(n),
            $"{n.FeatureNamespace}.Queries.GetListByDynamic",
            "Can.Core.Persistence.Dynamic",
            "Can.Core.Application.Requests",
            "Can.Core.Application.Responses");
        return plan;
    }

    private static void AddDynamicQueryFiles(GenerationPlan plan, CrudNames n, string feature)
    {
        string E = n.Entity;
        plan.AddFile(Path.Combine(feature, "Queries", "GetListByDynamic", $"GetListByDynamic{E}Query.cs"), CrudTemplates.GetListByDynamicQuery(n));
        plan.AddFile(Path.Combine(feature, "Queries", "GetListByDynamic", $"GetListByDynamic{E}QueryValidator.cs"), CrudTemplates.PageQueryValidator(n, "GetListByDynamic"));
        plan.AddFile(
            Path.Combine(feature, "Queries", "GetListByDynamic", $"GetListByDynamic{E}ListItemDto.cs"),
            CrudTemplates.Response(n, "Queries.GetListByDynamic", $"GetListByDynamic{E}ListItemDto", n.ResponseFields, isDto: true));
    }

    /// <summary>İşareti içeren dosyayı katmanda arar; bulunamazsa beklenen yolu döner (uygulamada "işaret yok" uyarısı verilir).</summary>
    internal static string MarkerFile(string directory, string marker, string expected)
    {
        string text = "// " + marker;
        return SolutionLayout.SourceFiles(directory).FirstOrDefault(f => File.ReadLines(f).Any(l => l.Trim() == text))
            ?? Path.Combine(directory, expected);
    }
}

/// <summary>Bir entity için üretilen tüm adlar ve alan listeleri.</summary>
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
        Repository = $"_{Names.Camel(entity.Name)}Repository";
        Rules = $"_{Names.Camel(entity.Name)}BusinessRules";
        FeatureNamespace = $"{Root}.Application.Features.{Plural}";

        List<MethodParameter> scalars = entity.ScalarProperties
            .Where(p => p.Name != "Id")
            .Select(p => new MethodParameter(p.Name, p.Type))
            .ToList();

        CreateFields = entity.Factory?.Parameters.Select(Pascalize).ToList() ?? scalars;
        UpdateFields = entity.Update?.Parameters.Select(Pascalize).ToList() ?? scalars;

        var response = new List<MethodParameter> { new("Id", entity.IdType) };
        response.AddRange(scalars);
        if (entity.IsAudited)
        {
            response.Add(new MethodParameter("CreatedAt", "DateTimeOffset"));
            response.Add(new MethodParameter("UpdatedAt", "DateTimeOffset?"));
        }

        ResponseFields = response;

        // Fabrika metodu yoksa eşleyici public constructor ya da public setter'larla oluşturur.
        CreateWithMapper = entity.Factory is null && entity.HasPublicConstructor;
        UpdateWithMapper = entity.Update is null
            && UpdateFields.Count > 0
            && UpdateFields.All(f => entity.Properties.FirstOrDefault(p => p.Name == f.Name) is { HasPublicSetter: true });
    }

    public EntityModel Model { get; }

    public CrudOptions Options { get; init; } = new();

    public string Root { get; }

    public string Entity { get; }

    public string Plural { get; }

    /// <summary>"Product category" (metinler).</summary>
    public string Words { get; }

    /// <summary>"product-categories" (yetki adları).</summary>
    public string Route { get; }

    /// <summary>"product_category" (hata kodu öneki).</summary>
    public string ErrorPrefix { get; }

    /// <summary>"brand" (yerel değişken).</summary>
    public string Variable { get; }

    /// <summary>"_brandRepository".</summary>
    public string Repository { get; }

    /// <summary>"_brandBusinessRules".</summary>
    public string Rules { get; }

    public string IdType => Model.IdType;

    public string FeatureNamespace { get; }

    public string ConstantsNamespace => FeatureNamespace + ".Constants";

    public string RulesNamespace => FeatureNamespace + ".Rules";

    /// <summary>Oluşturma komutunun alanları: fabrika metodunun parametreleri, yoksa entity'nin düz özellikleri.</summary>
    public IReadOnlyList<MethodParameter> CreateFields { get; }

    /// <summary>Güncelleme komutunun alanları (Id hariç): Update metodunun parametreleri, yoksa düz özellikler.</summary>
    public IReadOnlyList<MethodParameter> UpdateFields { get; }

    /// <summary>Yanıt ve liste DTO'larının alanları (Id, düz özellikler, denetim tarihleri).</summary>
    public IReadOnlyList<MethodParameter> ResponseFields { get; }

    /// <summary>Oluşturma eşleyiciyle (<c>CreateMap&lt;CreateXCommand, X&gt;</c>) yapılır.</summary>
    public bool CreateWithMapper { get; }

    /// <summary>Güncelleme eşleyiciyle (<c>mapper.Map(request, entity)</c>) yapılır.</summary>
    public bool UpdateWithMapper { get; }

    /// <summary>Kimlik veritabanında üretiliyor mu (int/long): yanıt dönmeden önce kaydedilmeli.</summary>
    public bool DatabaseGeneratedId => Model.IdType is not ("Guid" or "string" or "String");

    private static MethodParameter Pascalize(MethodParameter parameter) => parameter with { Name = Names.Pascal(parameter.Name) };
}
