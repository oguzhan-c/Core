using System.Globalization;
using Can.Core.Application;
using Can.Core.Reporting.Export;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Reporting.AspNetCore;

/// <summary>Raporlama uçlarının ayarları.</summary>
public sealed class CanReportingEndpointOptions
{
    /// <summary>Her şeye erişen rol (kaynak yetkilerini geçer, paylaşılan raporları silebilir). Boşsa yok.</summary>
    public string? AdminRole { get; set; } = "Admin";

    /// <summary>Raporlamayı kullanmak için genel yetki (ör. <c>"reports.view"</c>); boşsa giriş yapmış herkes.</summary>
    public string? Permission { get; set; }

    /// <summary>Raporu tenant'taki herkesle paylaşmak için gereken yetki; boşsa herkes paylaşabilir.</summary>
    public string? SharePermission { get; set; }

    /// <summary>Kullanıcı başına en fazla kayıtlı rapor.</summary>
    public int MaxSavedReportsPerUser { get; set; } = 200;
}

public sealed record ReportSourceDto(string Name, string Caption);

public sealed record ReportSourceDetailDto(string Name, string Caption, IReadOnlyList<ReportField> Fields);

/// <param name="Definition">Rapor.</param>
/// <param name="Format">Dosya biçimi.</param>
public sealed record ReportExportRequest(ReportDefinition Definition, ReportExportFormat Format = ReportExportFormat.Xlsx)
{
    public string? Title { get; init; }

    /// <summary>Başlık altı satırı (ör. filtre özeti).</summary>
    public string? Subtitle { get; init; }

    public PdfPageSize PdfPageSize { get; init; } = PdfPageSize.A4Landscape;
}

public sealed record SaveReportRequest(string Name, ReportDefinition Definition)
{
    public string? Description { get; init; }

    public bool IsShared { get; init; }
}

public sealed record SavedReportSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    string DataSource,
    bool IsShared,
    bool IsOwner,
    bool CanEdit,
    string? OwnerName,
    DateTimeOffset UpdatedAt);

public sealed record SavedReportDto(
    Guid Id,
    string Name,
    string? Description,
    string DataSource,
    bool IsShared,
    bool IsOwner,
    bool CanEdit,
    string? OwnerName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ReportDefinition Definition);

public static class ReportingEndpoints
{
    /// <summary>
    /// Raporlama uçları (hepsi giriş ister):
    /// <list type="bullet">
    /// <item><c>GET  /sources</c>, <c>GET /sources/{name}</c>: kullanıcının erişebildiği veri kaynakları ve alanları.</item>
    /// <item><c>POST /run</c>: tanımı çalıştırır, <see cref="ReportResult"/> döner (ekran ve grafikler).</item>
    /// <item><c>POST /export</c>: tanımı çalıştırıp CSV, Excel ya da PDF dosyası döner.</item>
    /// <item><c>GET/POST /saved</c>, <c>GET/PUT/DELETE /saved/{id}</c>: kaydedilmiş raporlar (kendi + paylaşılan).</item>
    /// </list>
    /// Kaynak yetkisi (<see cref="IReportDataSource.Permission"/>) her çalıştırmada denetlenir; kayıtlı raporlar tenant'a
    /// göre ayrılır. Hatalar ProblemDetails (400 geçersiz tanım, 403 yetki yok, 404 yok).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanReporting()
    ///     .AddSource("sales", sp =&gt; sp.GetRequiredService&lt;AppDbContext&gt;().SalesRows, o =&gt; o.Permission = "reports.sales")
    ///     .UseEntityFrameworkStore&lt;AppDbContext&gt;();
    /// app.MapCanReporting("/api/reporting");
    /// </code>
    /// </example>
    public static RouteGroupBuilder MapCanReporting(this IEndpointRouteBuilder endpoints, string basePath = "/api/reporting", Action<CanReportingEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new CanReportingEndpointOptions();
        configure?.Invoke(options);

        RouteGroupBuilder group = endpoints.MapGroup(basePath).WithTags("Reporting").RequireAuthorization();

        group.MapGet("/sources", (HttpContext http) =>
        {
            var scope = new Scope(http, options);
            if (!scope.Access.CanUseReporting)
                return Forbidden();
            return TypedResults.Ok(scope.Reports.DataSources.Where(scope.Access.CanUse).Select(s => new ReportSourceDto(s.Name, s.Caption)).ToArray());
        });

        group.MapGet("/sources/{name}", (string name, HttpContext http) =>
        {
            var scope = new Scope(http, options);
            if (Source(scope, name, out IReportDataSource? source) is { } error)
                return error;
            return TypedResults.Ok(new ReportSourceDetailDto(source!.Name, source.Caption, source.Fields));
        });

        group.MapPost("/run", async (ReportDefinition definition, HttpContext http) =>
        {
            (ReportResult? result, IResult? error) = await RunAsync(definition, new Scope(http, options), http.RequestAborted).ConfigureAwait(false);
            return error ?? TypedResults.Ok(result);
        });

        group.MapPost("/export", async (ReportExportRequest request, HttpContext http) =>
        {
            if (request?.Definition is null)
                return Invalid("Rapor tanımı boş.");

            var scope = new Scope(http, options);
            (ReportResult? result, IResult? error) = await RunAsync(request.Definition, scope, http.RequestAborted).ConfigureAwait(false);
            if (error is not null)
                return error;

            string title = FirstText(request.Title, request.Definition.Title, scope.Reports.Find(request.Definition.DataSource!)?.Caption) ?? "Rapor";
            byte[] file = ReportExporter.Export(result!, request.Format, new ReportExportOptions
            {
                Culture = CultureInfo.CurrentCulture,
                Title = title,
                Subtitle = request.Subtitle,
                PdfPageSize = request.PdfPageSize,
                GeneratedAt = scope.Clock.GetLocalNow(),
            });
            return TypedResults.File(file, ReportExporter.ContentType(request.Format), FileName(title) + ReportExporter.FileExtension(request.Format));
        });

        MapSaved(group.MapGroup("/saved"), options);
        return group;
    }

    // ------------------------------------------------------------------ kaydedilmiş raporlar

    private static void MapSaved(RouteGroupBuilder saved, CanReportingEndpointOptions options)
    {
        saved.MapGet("/", async (HttpContext http) =>
        {
            var scope = new Scope(http, options);
            if (!scope.Access.CanUseReporting || scope.User.Id is null)
                return Forbidden();

            IReadOnlyList<SavedReport> list = await scope.Store.ListAsync(scope.TenantId, scope.User.Id, http.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(list
                .Where(r => scope.Reports.Find(r.DataSource) is { } source && scope.Access.CanUse(source))
                .Select(r => new SavedReportSummaryDto(r.Id, r.Name, r.Description, r.DataSource, r.IsShared, r.OwnerId == scope.User.Id, scope.Access.CanEdit(r), r.OwnerName, r.UpdatedAt))
                .ToArray());
        });

        saved.MapGet("/{id:guid}", async (Guid id, HttpContext http) =>
        {
            var scope = new Scope(http, options);
            SavedReport? report = await scope.Store.FindAsync(id, http.RequestAborted).ConfigureAwait(false);
            if (Visible(report, scope) is { } error)
                return error;
            return TypedResults.Ok(ToDto(report!, scope.Access));
        });

        saved.MapPost("/", async (SaveReportRequest request, HttpContext http) =>
        {
            var scope = new Scope(http, options);
            if (Validate(request, scope) is { } error)
                return error;

            IReadOnlyList<SavedReport> existing = await scope.Store.ListAsync(scope.TenantId, scope.User.Id!, http.RequestAborted).ConfigureAwait(false);
            if (existing.Count(r => r.OwnerId == scope.User.Id) >= options.MaxSavedReportsPerUser)
                return Invalid($"En fazla {options.MaxSavedReportsPerUser} rapor kaydedilebilir.");

            DateTimeOffset now = scope.Clock.GetUtcNow();
            var report = new SavedReport
            {
                TenantId = scope.TenantId,
                OwnerId = scope.User.Id!,
                OwnerName = scope.User.UserName ?? scope.User.Email,
                CreatedAt = now,
            };
            Apply(report, request, now);
            await scope.Store.AddAsync(report, http.RequestAborted).ConfigureAwait(false);
            return TypedResults.Created($"{http.Request.PathBase}{http.Request.Path.Value?.TrimEnd('/')}/{report.Id}", ToDto(report, scope.Access));
        });

        saved.MapPut("/{id:guid}", async (Guid id, SaveReportRequest request, HttpContext http) =>
        {
            var scope = new Scope(http, options);
            SavedReport? report = await scope.Store.FindAsync(id, http.RequestAborted).ConfigureAwait(false);
            if (Visible(report, scope) is { } notFound)
                return notFound;
            if (!scope.Access.CanEdit(report!))
                return Forbidden("Raporu yalnızca sahibi değiştirebilir.");
            if (Validate(request, scope) is { } error)
                return error;

            Apply(report!, request, scope.Clock.GetUtcNow());
            await scope.Store.UpdateAsync(report!, http.RequestAborted).ConfigureAwait(false);
            return TypedResults.Ok(ToDto(report!, scope.Access));
        });

        saved.MapDelete("/{id:guid}", async (Guid id, HttpContext http) =>
        {
            var scope = new Scope(http, options);
            SavedReport? report = await scope.Store.FindAsync(id, http.RequestAborted).ConfigureAwait(false);
            if (Visible(report, scope) is { } notFound)
                return notFound;
            if (!scope.Access.CanEdit(report!))
                return Forbidden("Raporu yalnızca sahibi silebilir.");

            await scope.Store.DeleteAsync(report!, http.RequestAborted).ConfigureAwait(false);
            return TypedResults.NoContent();
        });
    }

    private static IResult? Validate(SaveReportRequest? request, Scope scope)
    {
        Access access = scope.Access;
        if (!access.CanUseReporting || access.User.Id is null)
            return Forbidden();
        if (request?.Definition is null)
            return Invalid("Rapor tanımı boş.");

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
            errors["name"] = ["Rapor adı zorunlu."];
        else if (request.Name.Trim().Length > SavedReport.NameMaxLength)
            errors["name"] = [$"Rapor adı en fazla {SavedReport.NameMaxLength} karakter olabilir."];
        if (request.Description?.Length > SavedReport.DescriptionMaxLength)
            errors["description"] = [$"Açıklama en fazla {SavedReport.DescriptionMaxLength} karakter olabilir."];
        if (errors.Count > 0)
            return TypedResults.ValidationProblem(errors);

        if (Source(scope, request.Definition.DataSource, out _) is { } error)
            return error;
        if (request.IsShared && !access.CanShare)
            return Forbidden("Rapor paylaşma yetkin yok.");
        return null;
    }

    private static void Apply(SavedReport report, SaveReportRequest request, DateTimeOffset now)
    {
        report.Name = request.Name.Trim();
        report.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        report.IsShared = request.IsShared;
        report.WriteDefinition(request.Definition);
        report.UpdatedAt = now;
    }

    /// <summary>Rapor yoksa, başka tenant'taysa, başkasınınsa ve paylaşılmamışsa ya da kaynağına erişim yoksa 404.</summary>
    private static IResult? Visible(SavedReport? report, Scope scope)
    {
        Access access = scope.Access;
        if (!access.CanUseReporting || access.User.Id is null)
            return Forbidden();
        if (report is null
            || report.TenantId != scope.TenantId
            || (report.OwnerId != access.User.Id && !report.IsShared)
            || scope.Reports.Find(report.DataSource) is not { } source
            || !access.CanUse(source))
        {
            return NotFound("Rapor bulunamadı.");
        }

        return null;
    }

    private static SavedReportDto ToDto(SavedReport r, Access access) =>
        new(r.Id, r.Name, r.Description, r.DataSource, r.IsShared, r.OwnerId == access.User.Id, access.CanEdit(r), r.OwnerName, r.CreatedAt, r.UpdatedAt, r.ReadDefinition());

    // ------------------------------------------------------------------ ortak

    private static async Task<(ReportResult? Result, IResult? Error)> RunAsync(ReportDefinition? definition, Scope scope, CancellationToken ct)
    {
        if (definition is null)
            return (null, Invalid("Rapor tanımı boş."));
        if (Source(scope, definition.DataSource, out _) is { } error)
            return (null, error);

        try
        {
            return (await scope.Reports.RunAsync(definition, ct).ConfigureAwait(false), null);
        }
        catch (ReportDefinitionException e)
        {
            return (null, Invalid(e.Message));
        }
    }

    private static IResult? Source(Scope scope, string? name, out IReportDataSource? source)
    {
        Access access = scope.Access;
        IReportService reports = scope.Reports;
        source = null;
        if (!access.CanUseReporting)
            return Forbidden();
        if (string.IsNullOrWhiteSpace(name) || reports.Find(name) is not { } found)
            return NotFound($"'{name}' veri kaynağı yok.");
        if (!access.CanUse(found))
            return Forbidden("Bu veri kaynağına erişim yetkin yok.");
        source = found;
        return null;
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Dosya adı: geçersiz karakterler <c>_</c>, en çok 80 karakter.</summary>
    internal static string FileName(string title)
    {
        char[] invalid = [.. Path.GetInvalidFileNameChars(), '"', '\\', '/', ':', '*', '?', '<', '>', '|'];
        string name = new(title.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        name = name.Length > 80 ? name[..80] : name;
        return string.IsNullOrWhiteSpace(name) ? "rapor" : name;
    }

    private static IResult Invalid(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Geçersiz rapor", extensions: new Dictionary<string, object?> { ["code"] = "report.invalid" });

    private static IResult Forbidden(string detail = "Raporlama yetkin yok.") =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden, title: "Yetki yok", extensions: new Dictionary<string, object?> { ["code"] = "report.forbidden" });

    private static IResult NotFound(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Bulunamadı", extensions: new Dictionary<string, object?> { ["code"] = "report.not_found" });

    /// <summary>İsteğin servisleri (kayıtlı değilse varsayılanlar: anonim kullanıcı, tenant yok, sistem saati).</summary>
    private sealed class Scope
    {
        public Scope(HttpContext http, CanReportingEndpointOptions options)
        {
            IServiceProvider services = http.RequestServices;
            Reports = services.GetRequiredService<IReportService>();
            Store = services.GetService<ISavedReportStore>() ?? throw new InvalidOperationException("ISavedReportStore kayıtlı değil: AddCanReporting() çağrılmalı.");
            User = services.GetService<ICurrentUser>() ?? NullCurrentUser.Instance;
            TenantId = (services.GetService<ICurrentTenant>() ?? NullCurrentTenant.Instance).Id?.ToString();
            Clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
            Access = new Access(User, options);
        }

        public IReportService Reports { get; }

        public ISavedReportStore Store { get; }

        public ICurrentUser User { get; }

        public string? TenantId { get; }

        public TimeProvider Clock { get; }

        public Access Access { get; }
    }

    private sealed class Access(ICurrentUser user, CanReportingEndpointOptions options)
    {
        public ICurrentUser User => user;

        public bool IsAdmin => options.AdminRole is { Length: > 0 } role && user.IsInRole(role);

        public bool CanUseReporting => user.IsAuthenticated && (IsAdmin || options.Permission is null || user.HasPermission(options.Permission));

        public bool CanShare => IsAdmin || options.SharePermission is null || user.HasPermission(options.SharePermission);

        public bool CanUse(IReportDataSource source) =>
            CanUseReporting && (IsAdmin || string.IsNullOrEmpty(source.Permission) || user.HasPermission(source.Permission));

        /// <summary>Sahip her zaman; yönetici paylaşılanları.</summary>
        public bool CanEdit(SavedReport report) => report.OwnerId == user.Id || (IsAdmin && report.IsShared);
    }
}
