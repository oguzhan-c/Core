using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Can.Core.Generator;

/// <summary>
/// Domain projesindeki entity'leri sözdizimi ağacından okur (derleme yapılmaz, bu yüzden çözüm derlenmese de çalışır).
/// Entity: <c>Entity&lt;TId&gt;</c>, <c>AggregateRoot&lt;TId&gt;</c> ya da Audited türevlerinden birinden türeyen sınıf.
/// </summary>
public static class EntityReader
{
    private static readonly HashSet<string> EntityBaseTypes = new(StringComparer.Ordinal)
    {
        "Entity", "AggregateRoot",
        "AuditedEntity", "AuditedAggregateRoot",
        "FullAuditedEntity", "FullAuditedAggregateRoot",
    };

    private static readonly HashSet<string> ScalarTypes = new(StringComparer.Ordinal)
    {
        "string", "String", "char", "bool", "Boolean",
        "byte", "short", "int", "long", "float", "double", "decimal",
        "Byte", "Int16", "Int32", "Int64", "Single", "Double", "Decimal",
        "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Uri",
    };

    private static readonly string[] CollectionTypes =
    [
        "IReadOnlyCollection", "IReadOnlyList", "ICollection", "IList", "IEnumerable", "List", "HashSet", "ISet", "IReadOnlySet",
    ];

    /// <summary>Domain'deki tüm entity adları (alfabetik).</summary>
    public static IReadOnlyList<(string Name, string FilePath)> List(SolutionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Parse(layout.Domain)
            .SelectMany(file => file.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(IsEntity).Select(c => (c.Identifier.Text, file.Path)))
            .OrderBy(e => e.Text, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Entity'yi okur. <paramref name="nameOrPath"/> sınıf adı (<c>Brand</c>) ya da entity dosyasının yolu olabilir.
    /// </summary>
    public static EntityModel Read(SolutionLayout layout, string nameOrPath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrEmpty(nameOrPath);

        List<(string Path, CompilationUnitSyntax Root)> files = Parse(layout.Domain).ToList();
        HashSet<string> enums = files.SelectMany(f => f.Root.DescendantNodes().OfType<EnumDeclarationSyntax>()).Select(e => e.Identifier.Text).ToHashSet(StringComparer.Ordinal);

        bool isPath = nameOrPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        string? fullPath = isPath ? Path.GetFullPath(nameOrPath) : null;

        var candidates = files
            .Where(f => fullPath is null || string.Equals(Path.GetFullPath(f.Path), fullPath, StringComparison.Ordinal))
            .SelectMany(f => f.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => (File: f, Class: c)))
            .Where(x => IsEntity(x.Class) && (isPath || x.Class.Identifier.Text == nameOrPath))
            .ToList();

        if (candidates.Count == 0)
        {
            throw new GeneratorException(isPath
                ? $"'{nameOrPath}' dosyasında entity bulunamadı (Entity<TId> / AggregateRoot<TId> ya da Audited türevlerinden türemeli) ya da dosya {layout.Domain} altında değil."
                : $"'{nameOrPath}' entity'si {layout.Domain} altında bulunamadı (Entity<TId> / AggregateRoot<TId> ya da Audited türevlerinden türemeli).");
        }

        if (candidates.Count > 1)
            throw new GeneratorException($"Birden fazla entity bulundu: {string.Join(", ", candidates.Select(c => c.Class.Identifier.Text))}. Entity adını ver.");

        var (file, declaration) = candidates[0];
        return ToModel(file.Path, declaration, enums);
    }

    private static EntityModel ToModel(string path, ClassDeclarationSyntax declaration, HashSet<string> enums)
    {
        GenericNameSyntax baseType = EntityBase(declaration)!;
        string name = declaration.Identifier.Text;
        string[] interfaces = declaration.BaseList!.Types.Skip(1).Select(t => t.Type.ToString()).ToArray();

        // Kısmi (partial) sınıfın diğer parçaları ayrı dosyada olabilir; üyeler bu bildirimden okunur.
        List<EntityProperty> properties = declaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(p => IsPublic(p.Modifiers) && !p.Modifiers.Any(SyntaxKind.StaticKeyword) && HasGetter(p) && p.ExpressionBody is null)
            .Where(p => !p.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString() is "NotMapped" or "NotMappedAttribute"))
            .Select(p => ToProperty(p, enums))
            .ToList();

        HashSet<string> constants = declaration.Members
            .OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword) && f.Declaration.Type.ToString() is "int" or "Int32")
            .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
            .ToHashSet(StringComparer.Ordinal);

        List<MethodDeclarationSyntax> methods = declaration.Members.OfType<MethodDeclarationSyntax>().Where(m => IsPublic(m.Modifiers)).ToList();

        EntityMethod? factory = methods
            .Where(m => m.Modifiers.Any(SyntaxKind.StaticKeyword) && m.Identifier.Text == "Create")
            .Where(m => m.ReturnType.ToString() == name || m.ReturnType.ToString() == $"Result<{name}>")
            .OrderByDescending(m => m.ParameterList.Parameters.Count)
            .Select(ToMethod)
            .FirstOrDefault();

        EntityMethod? update = methods
            .Where(m => !m.Modifiers.Any(SyntaxKind.StaticKeyword) && m.Identifier.Text == "Update")
            .OrderByDescending(m => m.ParameterList.Parameters.Count)
            .Select(ToMethod)
            .FirstOrDefault();

        string baseName = baseType.Identifier.Text;
        return new EntityModel
        {
            Name = name,
            Namespace = Namespace(declaration),
            IdType = baseType.TypeArgumentList.Arguments[0].ToString(),
            BaseType = baseName,
            FilePath = path,
            IsAudited = baseName.Contains("Audited", StringComparison.Ordinal),
            IsSoftDeletable = baseName.StartsWith("FullAudited", StringComparison.Ordinal) || interfaces.Contains("ISoftDeletable"),
            IsMultiTenant = interfaces.Any(i => i.StartsWith("IMultiTenant", StringComparison.Ordinal)),
            Properties = properties,
            Factory = factory,
            Update = update,
            IntConstants = constants,
        };
    }

    private static EntityProperty ToProperty(PropertyDeclarationSyntax property, HashSet<string> enums)
    {
        TypeSyntax type = property.Type;
        bool nullable = type is NullableTypeSyntax;
        TypeSyntax bare = type is NullableTypeSyntax n ? n.ElementType : type;
        string bareName = bare is QualifiedNameSyntax q ? q.Right.ToString() : bare.ToString();

        PropertyKind kind =
            ScalarTypes.Contains(bareName) ? PropertyKind.Scalar
            : enums.Contains(bareName) ? PropertyKind.Enum
            : bare is ArrayTypeSyntax || (bare is GenericNameSyntax g && CollectionTypes.Contains(g.Identifier.Text)) ? PropertyKind.Collection
            : PropertyKind.Complex;

        bool hasSetter = property.AccessorList?.Accessors.Any(a => a.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration) == true;
        return new EntityProperty(property.Identifier.Text, type.ToString(), nullable, kind, hasSetter);
    }

    private static EntityMethod ToMethod(MethodDeclarationSyntax method) =>
        new(
            method.Identifier.Text,
            method.ParameterList.Parameters.Select(p => new MethodParameter(p.Identifier.Text, p.Type?.ToString() ?? "object")).ToList(),
            method.ReturnType.ToString());

    private static bool IsEntity(ClassDeclarationSyntax declaration) =>
        !declaration.Modifiers.Any(SyntaxKind.AbstractKeyword) && EntityBase(declaration) is not null;

    private static GenericNameSyntax? EntityBase(ClassDeclarationSyntax declaration)
    {
        TypeSyntax? first = declaration.BaseList?.Types.FirstOrDefault()?.Type;
        GenericNameSyntax? generic = first switch
        {
            GenericNameSyntax g => g,
            QualifiedNameSyntax { Right: GenericNameSyntax g } => g,
            _ => null,
        };

        return generic is not null && EntityBaseTypes.Contains(generic.Identifier.Text) && generic.TypeArgumentList.Arguments.Count == 1 ? generic : null;
    }

    private static bool HasGetter(PropertyDeclarationSyntax property) =>
        property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration) && a.Body is null && a.ExpressionBody is null) == true;

    private static bool IsPublic(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.PublicKeyword);

    private static string Namespace(SyntaxNode node) =>
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty;

    private static IEnumerable<(string Path, CompilationUnitSyntax Root)> Parse(string directory)
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview);
        foreach (string file in SolutionLayout.SourceFiles(directory))
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file);
            yield return (file, tree.GetCompilationUnitRoot());
        }
    }
}
