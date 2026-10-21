namespace CanFx.Reporting;

/// <summary>
/// Veri kaynağı yetkisini servis katmanında denetler. Kayıtlıysa <see cref="IReportService.RunAsync"/> her çalıştırmada
/// sorar; kayıtlı değilse denetim çağırana (ör. <c>MapCanReporting</c> uç noktaları) bırakılır.
/// </summary>
/// <example>
/// <code>
/// services.AddScoped&lt;IReportAccessPolicy&gt;(sp =&gt; new DelegateReportAccessPolicy(source =&gt;
///     source.Permission is null || sp.GetRequiredService&lt;ICurrentUser&gt;().HasPermission(source.Permission)));
/// </code>
/// </example>
public interface IReportAccessPolicy
{
    bool CanUse(IReportDataSource source);
}

/// <summary>Temsilciyle <see cref="IReportAccessPolicy"/>.</summary>
public sealed class DelegateReportAccessPolicy(Func<IReportDataSource, bool> canUse) : IReportAccessPolicy
{
    public bool CanUse(IReportDataSource source) => canUse(source);
}

/// <summary>Kullanıcının veri kaynağını kullanma yetkisi yok.</summary>
public sealed class ReportAccessDeniedException : Exception
{
    public ReportAccessDeniedException() { }

    public ReportAccessDeniedException(string message)
        : base(message) { }

    public ReportAccessDeniedException(string message, Exception innerException)
        : base(message, innerException) { }
}
