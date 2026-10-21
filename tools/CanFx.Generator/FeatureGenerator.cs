namespace CanFx.Generator;

/// <summary>Var olan bir özelliğe (Features/{Özellik}) eklenen komut/sorgu seçenekleri.</summary>
public sealed record RequestOptions
{
    public bool Caching { get; init; } = true;

    public bool Logging { get; init; } = true;

    /// <summary>Yalnızca komutlar.</summary>
    public bool Transaction { get; init; } = true;

    public bool Secured { get; init; } = true;

    /// <summary>Controller ucunun HTTP metodu: Get, Post, Put, Delete. Boşsa komutta Post, sorguda Get.</summary>
    public string? EndpointMethod { get; init; }
}

/// <summary>
/// Var olan bir özelliğe yeni komut ya da sorgu ekler (nArchitecture'daki <c>generate command/query</c>):
/// <code>
/// Features/{Özellik}/Commands/{Ad}/{Ad}Command.cs (+ Handler), {Ad}CommandValidator.cs, {Ad}Response.cs
/// Features/{Özellik}/Queries/{Ad}/{Ad}Query.cs (+ Handler), {Ad}QueryValidator.cs, {Ad}Response.cs
/// </code>
/// ve <c>{Özellik}OperationClaims</c>'e yetki, <c>{Özellik}Controller</c>'a uç ekler.
/// </summary>
public static class FeatureGenerator
{
    private static readonly string[] HttpMethods = ["Get", "Post", "Put", "Delete", "Patch"];

    /// <summary>Application/Features altındaki özellikler (alfabetik).</summary>
    public static IReadOnlyList<string> ListFeatures(SolutionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string features = Path.Combine(layout.Application, "Features");
        return Directory.Exists(features)
            ? Directory.EnumerateDirectories(features).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
    }

    public static GenerationPlan PlanCommand(SolutionLayout layout, string feature, string name, RequestOptions? options = null) =>
        Plan(layout, feature, name, isCommand: true, options ?? new RequestOptions());

    public static GenerationPlan PlanQuery(SolutionLayout layout, string feature, string name, RequestOptions? options = null) =>
        Plan(layout, feature, name, isCommand: false, options ?? new RequestOptions());

    private static GenerationPlan Plan(SolutionLayout layout, string feature, string name, bool isCommand, RequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string kind = isCommand ? "Command" : "Query";
        feature = Names.Pascal(feature.Trim());
        name = Names.Pascal(name.Trim());
        if (name.EndsWith(kind, StringComparison.Ordinal) && name.Length > kind.Length)
            name = name[..^kind.Length];

        string featureDirectory = Path.Combine(layout.Application, "Features", feature);
        if (!Directory.Exists(featureDirectory))
            throw new GeneratorException($"'{feature}' özelliği yok ({featureDirectory}). Var olanlar: {string.Join(", ", ListFeatures(layout))}");

        string claimsFile = Path.Combine(featureDirectory, "Constants", $"{feature}OperationClaims.cs");
        if (options.Secured && !File.Exists(claimsFile))
            throw new GeneratorException($"{feature}OperationClaims bulunamadı ({claimsFile}). Yetkisiz üretmek için güvenliği kapat (--secured kullanma).");

        string? method = options.EndpointMethod is { Length: > 0 } m ? Names.Pascal(m.ToLowerInvariant()) : null;
        if (method is not null && !HttpMethods.Contains(method))
            throw new GeneratorException($"Geçersiz HTTP metodu: {options.EndpointMethod} (Get, Post, Put, Delete, Patch).");
        method ??= isCommand ? "Post" : "Get";

        // Önbellek etiketi için özelliğin entity'si (Brands → Brand); yoksa etiket yazılmaz.
        string singular = Names.Singular(feature);
        (string Name, string FilePath)? entityEntry = EntityReader.List(layout).FirstOrDefault(e => e.Name == singular) is { Name: not null } found ? found : null;
        EntityModel? entity = entityEntry is { } entry ? EntityReader.Read(layout, entry.FilePath) : null;

        var n = new RequestNames(layout.RootNamespace, feature, name, kind, entity, method);
        var plan = new GenerationPlan();
        string folder = Path.Combine(featureDirectory, isCommand ? "Commands" : "Queries", name);

        plan.AddFile(Path.Combine(folder, $"{name}{kind}.cs"), FeatureTemplates.Request(n, isCommand, options));
        plan.AddFile(Path.Combine(folder, $"{name}{kind}Validator.cs"), FeatureTemplates.Validator(n));
        plan.AddFile(Path.Combine(folder, $"{name}Response.cs"), FeatureTemplates.Response(n));

        if (options.Secured)
        {
            plan.Inject(claimsFile, "can-gen:operation-claims", $"public const string {name} = \"{Names.Kebab(feature)}.{Names.Kebab(name)}\";");
            plan.Inject(claimsFile, "can-gen:operation-claims-all", $"({name}, \"{Names.Words(name)}\"),");
        }

        string controller = Path.Combine(layout.WebApi, "Controllers", $"{feature}Controller.cs");
        plan.Inject(controller, "can-gen:endpoints", FeatureTemplates.Endpoint(n, isCommand), n.Namespace);
        return plan;
    }
}

internal sealed record RequestNames(string Root, string Feature, string Name, string Kind, EntityModel? Entity, string Method)
{
    public string Namespace => $"{Root}.Application.Features.{Feature}.{(Kind == "Command" ? "Commands" : "Queries")}.{Name}";

    public string ConstantsNamespace => $"{Root}.Application.Features.{Feature}.Constants";

    public string Type => Name + Kind;

    public string Response => Name + "Response";
}

internal static class FeatureTemplates
{
    private const string Header = "// can-gen ile üretildi; artık senin kodun, istediğin gibi değiştir.";

    public static string Request(RequestNames n, bool isCommand, RequestOptions options)
    {
        var interfaces = new List<string> { $"IRequest<{n.Response}>" };
        if (options.Secured)
            interfaces.Add("ISecuredRequest");
        if (isCommand && options.Logging)
            interfaces.Add("ILoggableRequest");
        if (isCommand && options.Transaction)
            interfaces.Add("ITransactionalRequest");

        bool cacheTag = options.Caching && n.Entity is not null;
        if (cacheTag)
            interfaces.Add(isCommand ? "ICacheRemoverRequest" : "ICachableRequest");

        var members = new List<string>();
        if (options.Secured)
        {
            string claims = isCommand
                ? $"{n.Feature}OperationClaims.Admin, {n.Feature}OperationClaims.Write, {n.Feature}OperationClaims.{n.Name}"
                : $"{n.Feature}OperationClaims.Admin, {n.Feature}OperationClaims.Read, {n.Feature}OperationClaims.{n.Name}";
            members.Add($"    [JsonIgnore]\n    public IReadOnlyCollection<string> Permissions => [{claims}];");
        }

        if (cacheTag && isCommand)
        {
            members.Add($"    /// <summary>Bu komuttan sonra {n.Entity!.Name} önbelleği silinir (kayıt değişince zaten kendiliğinden silinir).</summary>\n    [JsonIgnore]\n    public IReadOnlyCollection<string> CacheTagsToRemove => [CanFx.Application.CacheTags.Entity<{n.Entity.Name}>()];");
        }
        else if (cacheTag)
        {
            members.Add($"    /// <summary>TODO(can-gen): Anahtara sorgunun parametrelerini ekle (ör. $\"{n.Name}({{Id}})\").</summary>\n    [JsonIgnore]\n    public string CacheKey => \"{n.Name}\";\n\n    [JsonIgnore]\n    public IReadOnlyCollection<string> CacheTags => [CanFx.Application.CacheTags.Entity<{n.Entity!.Name}>()];");
        }
        else if (options.Caching)
        {
            members.Add($"    // TODO(can-gen): Önbellek için {Names.Singular(n.Feature)} entity'si bulunamadı; gerekirse {(isCommand ? "ICacheRemoverRequest" : "ICachableRequest")} ekle.");
        }

        string body = members.Count == 0 ? string.Empty : "\n\n" + string.Join("\n\n", members);
        string usings = string.Join("\n", new[]
        {
            "using System.Text.Json.Serialization;",
            "using CanFx.Application;",
            "using CanFx.Mediator;",
            options.Secured ? $"using {n.ConstantsNamespace};" : null,
            cacheTag ? $"using {n.Entity!.Namespace};" : null,
        }.OfType<string>());

        string what = isCommand ? "komutun" : "sorgunun";
        return $$"""
            {{Header}}
            {{usings}}

            namespace {{n.Namespace}};

            /// <summary>TODO(can-gen): {{Names.Pascal(what)}} ne yaptığını yaz.</summary>
            public sealed class {{n.Type}} : {{string.Join(", ", interfaces)}}
            {
                // TODO(can-gen): {{Names.Pascal(what)}} alanlarını ekle (ör. public Guid Id { get; set; }).{{body}}
            }

            public sealed class {{n.Type}}Handler : IRequestHandler<{{n.Type}}, {{n.Response}}>
            {
                // TODO(can-gen): Gerekenleri (repository, IMapper, iş kuralları ...) constructor'dan al.

                public Task<{{n.Response}}> Handle({{n.Type}} request, CancellationToken cancellationToken)
                {
                    // TODO(can-gen): İşi yap ve yanıtı doldur.
                    return Task.FromResult(new {{n.Response}}());
                }
            }

            """;
    }

    public static string Validator(RequestNames n) => $$"""
        {{Header}}
        using FluentValidation;

        namespace {{n.Namespace}};

        public sealed class {{n.Type}}Validator : AbstractValidator<{{n.Type}}>
        {
            public {{n.Type}}Validator()
            {
                // TODO(can-gen): Doğrulama kurallarını ekle, ör. RuleFor(x => x.Id).NotEmpty();
            }
        }

        """;

    public static string Response(RequestNames n) => $$"""
        {{Header}}
        using CanFx.Application.Responses;

        namespace {{n.Namespace}};

        public sealed class {{n.Response}} : IResponse
        {
            // TODO(can-gen): Yanıt alanlarını ekle.
        }

        """;

    public static string Endpoint(RequestNames n, bool isCommand)
    {
        string source = n.Method is "Get" or "Delete" ? "FromQuery" : "FromBody";
        string variable = Names.Camel(n.Type);
        return $$"""
            [Http{{n.Method}}("{{n.Name}}")]
            public async Task<IActionResult> {{n.Name}}([{{source}}] {{n.Type}} {{variable}}, CancellationToken cancellationToken)
            {
                {{n.Response}} response = await Mediator.Send({{variable}}, cancellationToken);
                return Ok(response);
            }

            """;
    }
}
