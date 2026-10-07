namespace Can.Core.Search;

public enum SearchFieldType
{
    /// <summary>Kelimelere bölünür, tam metin aramada kullanılır (ad, açıklama).</summary>
    Text,

    /// <summary>Olduğu gibi saklanır: filtre, sıralama, toplama (kategori, kod, durum).</summary>
    Keyword,

    Long,
    Double,
    Boolean,
    Date,
}

/// <param name="Name">Alanın JSON adı (<c>unitPrice</c>, <c>category.name</c>).</param>
/// <param name="Type">Alan tipi.</param>
/// <param name="Analyzer">Metin alanlarının çözümleyicisi (<c>turkish</c>, <c>english</c>, <c>standard</c> ...).</param>
/// <param name="WithKeyword">Metin alanına filtre/sıralama için <c>alan.keyword</c> alt alanı eklenir.</param>
public sealed record SearchField(string Name, SearchFieldType Type, string? Analyzer = null, bool WithKeyword = false);

/// <summary>Bir belge tipinin dizini: adı ve alan eşlemesi. Eşlenmeyen alanları motor kendisi tahmin eder.</summary>
public sealed class SearchIndexDefinition
{
    internal SearchIndexDefinition(string name, Type documentType, IReadOnlyList<SearchField> fields, int? shards, int? replicas)
    {
        Name = name;
        DocumentType = documentType;
        Fields = fields;
        Shards = shards;
        Replicas = replicas;
    }

    /// <summary>Mantıksal ad (motor önek ekleyebilir: <c>myapp-products</c>).</summary>
    public string Name { get; }

    public Type DocumentType { get; }

    public IReadOnlyList<SearchField> Fields { get; }

    public int? Shards { get; }

    public int? Replicas { get; }
}

/// <summary>Alan eşlemesi kurucusu.</summary>
/// <example>
/// <code>
/// m =&gt; m.Text("name", "turkish", withKeyword: true).Text("description", "turkish").Keyword("categoryName").Double("unitPrice")
/// </code>
/// </example>
public sealed class SearchMappingBuilder
{
    private readonly List<SearchField> _fields = [];

    /// <summary>Birincil parça sayısı (Elasticsearch; tek sunucuda 1).</summary>
    public int? Shards { get; set; }

    /// <summary>Kopya sayısı (Elasticsearch; tek sunucuda 0).</summary>
    public int? Replicas { get; set; }

    public SearchMappingBuilder Text(string name, string? analyzer = null, bool withKeyword = false) =>
        Add(new SearchField(name, SearchFieldType.Text, analyzer, withKeyword));

    public SearchMappingBuilder Keyword(string name) => Add(new SearchField(name, SearchFieldType.Keyword));

    public SearchMappingBuilder Long(string name) => Add(new SearchField(name, SearchFieldType.Long));

    public SearchMappingBuilder Double(string name) => Add(new SearchField(name, SearchFieldType.Double));

    public SearchMappingBuilder Boolean(string name) => Add(new SearchField(name, SearchFieldType.Boolean));

    public SearchMappingBuilder Date(string name) => Add(new SearchField(name, SearchFieldType.Date));

    public SearchMappingBuilder Add(SearchField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(field.Name);
        _fields.RemoveAll(f => f.Name == field.Name);
        _fields.Add(field);
        return this;
    }

    internal SearchIndexDefinition Build(string name, Type documentType) => new(name, documentType, _fields.ToArray(), Shards, Replicas);
}
