using System.Linq.Expressions;

namespace Can.Core.Mapping;

/// <summary><c>CreateMap&lt;TSource, TDestination&gt;()</c> sonrasında kuralları tanımlamak için akıcı API.</summary>
public interface IMappingExpression<TSource, TDestination>
{
    /// <summary>
    /// Hedefteki bir üyenin nasıl doldurulacağını belirler.
    /// <code>
    /// .ForMember(d => d.CustomerName, o => o.MapFrom(s => s.Customer.FirstName + " " + s.Customer.LastName))
    /// .ForMember(d => d.InternalNote, o => o.Ignore())
    /// </code>
    /// </summary>
    IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMemberOptions<TSource>> options);

    /// <summary>
    /// Üyeyi adıyla yapılandırır. Record'larda constructor parametreleri için de kullanılabilir.
    /// </summary>
    IMappingExpression<TSource, TDestination> ForMember(
        string destinationMemberName,
        Action<IMemberOptions<TSource>> options);

    /// <summary>
    /// Eşleme bittikten sonra çalışır (yalnızca bellekte yapılan <c>Map</c> çağrılarında;
    /// <c>ProjectTo</c> ile veritabanına gönderilmez).
    /// </summary>
    IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> action);

    /// <summary>Ters yöndeki eşlemeyi (TDestination → TSource) konvansiyonla oluşturur.</summary>
    IMappingExpression<TDestination, TSource> ReverseMap();
}

/// <summary>Tek bir hedef üyenin kuralı.</summary>
public interface IMemberOptions<TSource>
{
    /// <summary>
    /// Değeri bu ifadeden al. Tip farklıysa (ör. <c>Customer</c> → <c>CustomerDto</c>,
    /// <c>int</c> → <c>long?</c>, enum → string) uygun dönüşüm otomatik yapılır.
    /// </summary>
    void MapFrom<TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember);

    /// <summary>Bu üyeyi doldurma.</summary>
    void Ignore();
}
