using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Can.Core.Security.Events;

/// <summary>Olayın önemi: log düzeyini ve alarm kurallarını belirler.</summary>
public enum SecurityEventSeverity
{
    /// <summary>Olağan (başarılı giriş, şifre değişti).</summary>
    Info = 0,

    /// <summary>Dikkat (başarısız giriş, hesap kilitlendi, geçersiz cihaz imzası).</summary>
    Warning = 1,

    /// <summary>Saldırı belirtisi (çalınmış refresh token'ın yeniden kullanımı, geri alınmış token'la istek).</summary>
    Critical = 2,
}

/// <summary>
/// Ortak olay tipleri (<c>alan.olay</c>). Uygulama kendi tiplerini de kullanabilir (<c>"payment.approved"</c>).
/// </summary>
public static class SecurityEventTypes
{
    public const string LoginSucceeded = "login.succeeded";
    public const string LoginFailed = "login.failed";
    public const string LoginFromNewDevice = "login.new_device";
    public const string AccountLocked = "account.locked";
    public const string AccountUnlocked = "account.unlocked";

    public const string TwoFactorSucceeded = "two_factor.succeeded";
    public const string TwoFactorFailed = "two_factor.failed";
    public const string TwoFactorEnabled = "two_factor.enabled";
    public const string TwoFactorDisabled = "two_factor.disabled";
    public const string RecoveryCodeUsed = "recovery_code.used";
    public const string RecoveryCodesGenerated = "recovery_codes.generated";

    public const string PasswordChanged = "password.changed";
    public const string PasswordResetRequested = "password.reset_requested";
    public const string PasswordReset = "password.reset";
    public const string EmailChanged = "email.changed";
    public const string ExternalLoginLinked = "external_login.linked";
    public const string ExternalLoginUnlinked = "external_login.unlinked";
    public const string PasskeyAdded = "passkey.added";
    public const string PasskeyRemoved = "passkey.removed";
    public const string RolesChanged = "roles.changed";

    public const string SessionRevoked = "session.revoked";
    public const string OtherSessionsRevoked = "session.revoked_others";
    public const string AllSessionsRevoked = "session.revoked_all";
    public const string RevokedTokenUsed = "session.revoked_token_used";
    public const string SecurityStampRejected = "session.stamp_rejected";
    public const string RefreshTokenReused = "refresh_token.reused";

    public const string DeviceRegistered = "device.registered";
    public const string DeviceRevoked = "device.revoked";
    public const string DeviceVerified = "device.verified";
    public const string DeviceVerificationFailed = "device.verification_failed";

    /// <summary>Tipin varsayılan önemi.</summary>
    /// <param name="type">Olay tipi.</param>
    public static SecurityEventSeverity DefaultSeverity(string type) => type switch
    {
        RefreshTokenReused or RevokedTokenUsed => SecurityEventSeverity.Critical,
        LoginFailed or LoginFromNewDevice or AccountLocked or TwoFactorFailed or TwoFactorDisabled or DeviceVerificationFailed
            or PasswordResetRequested or AllSessionsRevoked => SecurityEventSeverity.Warning,
        _ => SecurityEventSeverity.Info,
    };
}

/// <summary>
/// Güvenlik olayı. Kim, ne zaman, nereden (IP, cihaz, oturum), ne oldu ve neden. Parola, kod ya da token gibi gizli
/// değerler HİÇBİR alana yazılmamalı.
/// </summary>
public sealed record SecurityEvent
{
    /// <param name="type">Olay tipi (<see cref="SecurityEventTypes"/>).</param>
    /// <param name="userId">Kullanıcı (bilinmiyorsa, ör. olmayan e-postayla giriş, <see langword="null"/>).</param>
    public SecurityEvent(string type, string? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Type = type;
        UserId = userId;
        Severity = SecurityEventTypes.DefaultSeverity(type);
    }

    public string Type { get; }

    public string? UserId { get; init; }

    public SecurityEventSeverity Severity { get; init; }

    /// <summary>Boşsa yayınlanırken doldurulur.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Makinece okunur neden (<c>invalid_password</c>, <c>unknown_user</c>, <c>locked_out</c>). Kullanıcı adı denemesi gibi
    /// istemcinin gönderdiği serbest metni buraya yazma.
    /// </summary>
    public string? Reason { get; init; }

    public string? TenantId { get; init; }

    public string? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public string? SessionId { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>Dağıtık izleme kimliği (loglardan ve izlerden olaya gitmek için).</summary>
    public string? TraceId { get; init; }

    /// <summary>Ek bilgiler (ör. <c>{"provider": "google"}</c>).</summary>
    public IReadOnlyDictionary<string, string?> Properties { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Olayı bir yere yazar (log, veritabanı, SIEM, e-posta bildirimi ...). Birden çok sink kaydedilebilir.</summary>
/// <remarks>Sink hata fırlatırsa loglanır, diğer sink'ler ve asıl işlem (giriş ...) devam eder.</remarks>
public interface ISecurityEventSink
{
    ValueTask WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);
}

/// <summary>Olayı yazılmadan önce tamamlar (WebApi: IP, User-Agent, tenant, oturum, iz kimliği).</summary>
public interface ISecurityEventEnricher
{
    SecurityEvent Enrich(SecurityEvent securityEvent);
}

/// <summary>
/// Güvenlik olaylarını yayınlar. <c>AddCanSecurityEvents()</c> kaydeder; kaydedilmemişse core'un olay üreten yerleri
/// (oturum, cihaz, token geri alma) sessizce geçer.
/// </summary>
/// <example>
/// <code>
/// // giriş handler'ı
/// if (!passwordValid)
/// {
///     await events.PublishAsync(new SecurityEvent(SecurityEventTypes.LoginFailed, user.Id.ToString()) { Reason = "invalid_password" }, ct);
///     ...
/// }
/// </code>
/// </example>
public interface ISecurityEvents
{
    ValueTask PublishAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);
}

/// <summary>Güvenlik olayı metrikleri.</summary>
public static class SecurityEventTelemetry
{
    public const string Name = "Can.Core.Security";

    public static readonly Meter Meter = new(Name);

    /// <summary>
    /// Olaylar. Etiketler: <c>can.security.event</c> (tip), <c>can.security.severity</c>. Ani artışlar (başarısız giriş,
    /// <c>refresh_token.reused</c>) için alarm kur.
    /// </summary>
    public static readonly Counter<long> Events =
        Meter.CreateCounter<long>("can.security.events", unit: "{event}", description: "Güvenlik olayları");
}

/// <summary>Varsayılan yayıncı: zenginleştirir, sayar, sink'lere sırayla yazar.</summary>
public sealed partial class SecurityEventPublisher : ISecurityEvents
{
    private readonly IEnumerable<ISecurityEventEnricher> _enrichers;
    private readonly IEnumerable<ISecurityEventSink> _sinks;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SecurityEventPublisher> _logger;

    public SecurityEventPublisher(IEnumerable<ISecurityEventEnricher> enrichers, IEnumerable<ISecurityEventSink> sinks, TimeProvider timeProvider, ILogger<SecurityEventPublisher> logger)
    {
        _enrichers = enrichers;
        _sinks = sinks;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask PublishAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);

        if (securityEvent.OccurredAt == default)
            securityEvent = securityEvent with { OccurredAt = _timeProvider.GetUtcNow() };
        if (securityEvent.TraceId is null && Activity.Current is { } activity)
            securityEvent = securityEvent with { TraceId = activity.TraceId.ToString() };

        foreach (ISecurityEventEnricher enricher in _enrichers)
        {
            try
            {
                securityEvent = enricher.Enrich(securityEvent);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogEnricherFailed(exception, enricher.GetType().Name, securityEvent.Type);
            }
        }

        SecurityEventTelemetry.Events.Add(
            1,
            new KeyValuePair<string, object?>("can.security.event", securityEvent.Type),
            new KeyValuePair<string, object?>("can.security.severity", securityEvent.Severity.ToString()));

        foreach (ISecurityEventSink sink in _sinks)
        {
            try
            {
                await sink.WriteAsync(securityEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Olay yazılamadı diye giriş/çıkış başarısız olmasın.
                LogSinkFailed(exception, sink.GetType().Name, securityEvent.Type);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Güvenlik olayı sink'i {Sink} '{Type}' olayını yazamadı.")]
    private partial void LogSinkFailed(Exception exception, string sink, string type);

    [LoggerMessage(Level = LogLevel.Error, Message = "Güvenlik olayı zenginleştiricisi {Enricher} '{Type}' olayında hata verdi.")]
    private partial void LogEnricherFailed(Exception exception, string enricher, string type);
}

/// <summary>Olayları yapılandırılmış log olarak yazar (kategori <c>Can.Core.Security.Events</c>); düzey öneme göre.</summary>
public sealed partial class LoggingSecurityEventSink : ISecurityEventSink
{
    private readonly ILogger _logger;

    public LoggingSecurityEventSink(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger("Can.Core.Security.Events");
    }

    public ValueTask WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        LogLevel level = securityEvent.Severity switch
        {
            SecurityEventSeverity.Critical => LogLevel.Error,
            SecurityEventSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        Log(level, securityEvent.Type, securityEvent.UserId ?? "-", securityEvent.Reason ?? "-", securityEvent.IpAddress ?? "-",
            securityEvent.SessionId ?? "-", securityEvent.DeviceId ?? "-", securityEvent.TenantId ?? "-");
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Message = "Güvenlik olayı {SecurityEvent}: kullanıcı {UserId}, neden {Reason}, IP {IpAddress}, oturum {SessionId}, cihaz {DeviceId}, tenant {TenantId}")]
    private partial void Log(LogLevel level, string securityEvent, string userId, string reason, string ipAddress, string sessionId, string deviceId, string tenantId);
}

/// <summary><c>AddCanSecurityEvents(...)</c> ayarları.</summary>
public sealed class SecurityEventOptions
{
    /// <summary>Olaylar loglanır (<see cref="LoggingSecurityEventSink"/>). Varsayılan açık.</summary>
    public bool WriteToLog { get; set; } = true;
}

public static class SecurityEventServiceCollectionExtensions
{
    /// <summary>
    /// Güvenlik olayları: <see cref="ISecurityEvents"/>, log sink'i ve <c>can.security.events</c> metriği. Kendi
    /// sink'ini <c>services.AddSingleton&lt;ISecurityEventSink, ...&gt;()</c> ile ekle (e-posta bildirimi, SIEM);
    /// veritabanına yazmak için <c>AddCanSecurityEventStore&lt;TContext&gt;()</c>.
    /// </summary>
    public static IServiceCollection AddCanSecurityEvents(this IServiceCollection services, Action<SecurityEventOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new SecurityEventOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<ISecurityEvents>();
        services.AddScoped<ISecurityEvents, SecurityEventPublisher>(); // scoped: zenginleştiriciler isteğin bilgisini okur
        if (options.WriteToLog)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecurityEventSink, LoggingSecurityEventSink>());
        return services;
    }
}
