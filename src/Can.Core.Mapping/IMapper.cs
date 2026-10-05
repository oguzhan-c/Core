using System.Diagnostics.CodeAnalysis;

namespace Can.Core.Mapping;

/// <summary>Nesneleri <see cref="MappingProfile"/>'larda tanımlanan kurallara göre eşler.</summary>
public interface IMapper
{
    MapperConfiguration Configuration { get; }

    /// <summary>
    /// <paramref name="source"/>'u yeni bir <typeparamref name="TDestination"/> nesnesine eşler.
    /// Koleksiyonlar da desteklenir: <c>Map&lt;List&lt;ProductDto&gt;&gt;(products)</c>.
    /// Kaynak <see langword="null"/> ise <see langword="null"/> (default) döner.
    /// </summary>
    [return: NotNullIfNotNull(nameof(source))]
    TDestination? Map<TDestination>(object? source);

    /// <summary>Tipli kaynak ile eşleme. Kaynak <see langword="null"/> ise default döner.</summary>
    [return: NotNullIfNotNull(nameof(source))]
    TDestination? Map<TSource, TDestination>(TSource? source);

    /// <summary>
    /// <paramref name="source"/>'taki değerleri MEVCUT <paramref name="destination"/> nesnesine yazar
    /// (ör. update command'ında veritabanından okunan entity'yi güncellemek için) ve onu döndürür.
    /// </summary>
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);

    /// <summary>Tipleri çalışma anında belli olan eşlemeler için.</summary>
    object Map(object source, Type sourceType, Type destinationType);

    /// <summary>
    /// Sorguya <c>Select</c> olarak eklenir; EF Core yalnızca DTO'nun ihtiyaç duyduğu kolonları çeker.
    /// </summary>
    IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source);
}
