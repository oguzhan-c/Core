using System.Text.Json;
using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Settings.AspNetCore;

/// <summary><c>MapCanSettings</c> yetkileri.</summary>
public sealed class CanSettingsEndpointOptions
{
    /// <summary>Uygulama geneli ayarları görmek/değiştirmek için yetki.</summary>
    public string GlobalPermission { get; set; } = "settings.global";

    /// <summary>Kendi tenant'ının ayarlarını görmek/değiştirmek için yetki.</summary>
    public string TenantPermission { get; set; } = "settings.tenant";

    /// <summary>Her şeye izinli rol (boşsa yok).</summary>
    public string? AdminRole { get; set; } = "Admin";
}

/// <summary>Değiştirme isteği: <c>{ "value": 250 }</c>.</summary>
public sealed record SettingUpdate(JsonElement Value);

public static class SettingsEndpointExtensions
{
    /// <summary>Gizli ayarları ASP.NET Data Protection ile şifreler (çok sunucuda anahtar halkası paylaşılmalı: Redis ScaleOut).</summary>
    public static CanSettingsBuilder UseDataProtection(this CanSettingsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddDataProtection();
        builder.Services.RemoveAll<ISettingProtector>();
        builder.Services.AddSingleton<ISettingProtector, DataProtectionSettingProtector>();
        return builder;
    }

    /// <summary>
    /// Ayar uç noktaları (giriş gerekli; <c>UseCanExceptionHandler</c> hataları ProblemDetails'e çevirir):
    /// <code>
    /// GET    {base}?scope=tenant|global   liste (etkin değer, kaynak, varsayılan; gizliler gizli)
    /// PUT    {base}/{name}?scope=...      { "value": ... }
    /// DELETE {base}/{name}?scope=...      bu düzeydeki değeri sil (üst düzeye döner)
    /// GET    {base}/client                arayüz ayarları (IsVisibleToClients), giriş yapmış herkes
    /// </code>
    /// Düzey verilmezse aktif tenant varsa <c>tenant</c>, yoksa <c>global</c>.
    /// </summary>
    public static RouteGroupBuilder MapCanSettings(this IEndpointRouteBuilder endpoints, string basePath = "/api/settings", Action<CanSettingsEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new CanSettingsEndpointOptions();
        configure?.Invoke(options);

        RouteGroupBuilder group = endpoints.MapGroup(basePath).RequireAuthorization().WithTags("Settings");

        group.MapGet("/", async (string? scope, ISettingManager settings, ICurrentUser user, ICurrentTenant tenant, CancellationToken ct) =>
        {
            SettingScope level = Scope(scope, tenant);
            Authorize(user, options, level, permission: null);
            return TypedResults.Ok(await settings.ListAsync(level, ct).ConfigureAwait(false));
        });

        group.MapPut("/{name}", async (string name, string? scope, SettingUpdate body, ISettingManager settings, ICurrentUser user, ICurrentTenant tenant, CancellationToken ct) =>
        {
            SettingScope level = Scope(scope, tenant);
            Authorize(user, options, level, Find(settings, name).Permission);
            await settings.SetAsync(name, body.Value, level, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        group.MapDelete("/{name}", async (string name, string? scope, ISettingManager settings, ICurrentUser user, ICurrentTenant tenant, CancellationToken ct) =>
        {
            SettingScope level = Scope(scope, tenant);
            Authorize(user, options, level, Find(settings, name).Permission);
            await settings.ResetAsync(name, level, ct).ConfigureAwait(false);
            return TypedResults.NoContent();
        });

        group.MapGet("/client", async (ISettings settings, CancellationToken ct) =>
            TypedResults.Ok(await settings.GetClientValuesAsync(ct).ConfigureAwait(false)));

        return group;
    }

    private static SettingScope Scope(string? scope, ICurrentTenant tenant) => scope?.ToLowerInvariant() switch
    {
        null or "" => tenant.Id is null ? SettingScope.Global : SettingScope.Tenant,
        "global" => SettingScope.Global,
        "tenant" => SettingScope.Tenant,
        _ => throw new ValidationException("scope", "Düzey 'tenant' ya da 'global' olmalı.") { Code = "setting_scope_invalid" },
    };

    private static SettingDefinition Find(ISettingManager settings, string name) =>
        settings.Definitions.FirstOrDefault(d => d.Name == name) ?? throw new NotFoundException($"'{name}' ayarı yok.") { Code = "setting_not_found" };

    private static void Authorize(ICurrentUser user, CanSettingsEndpointOptions options, SettingScope scope, string? permission)
    {
        if (options.AdminRole is { Length: > 0 } admin && user.IsInRole(admin))
            return;
        string required = scope == SettingScope.Global ? options.GlobalPermission : options.TenantPermission;
        if (!user.HasPermission(required) || (permission is not null && !user.HasPermission(permission)))
            throw new ForbiddenException("Bu ayarları değiştirme yetkin yok.") { Code = "settings_forbidden" };
    }
}

internal sealed class DataProtectionSettingProtector(IDataProtectionProvider provider) : ISettingProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Can.Core.Settings.v1");

    public string Protect(string value) => _protector.Protect(value);

    public string Unprotect(string value) => _protector.Unprotect(value);
}
