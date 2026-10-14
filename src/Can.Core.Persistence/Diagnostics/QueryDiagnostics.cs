using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Can.Core.Application;
using Can.Core.Persistence.Outbox;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Can.Core.Persistence.Diagnostics;

/// <summary><c>AddCanQueryDiagnostics(...)</c> ayarları.</summary>
public sealed class QueryDiagnosticsOptions
{
    /// <summary>Bundan uzun süren komutlar uyarı olarak loglanır. <see cref="TimeSpan.Zero"/>: kapalı.</summary>
    public TimeSpan SlowQueryThreshold { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Her SQL'in başına kaynağını yazan bir yorum eklenir (<c>-- can: GetProductsQuery</c>). Veritabanının yavaş sorgu
    /// listesinde (pg_stat_statements, Query Store) sorgunun hangi koddan geldiği görünür.
    /// </summary>
    public bool TagQueries { get; set; } = true;

    /// <summary>Komut süreleri metrik olarak yayınlanır (<c>can.db.command.duration</c>, <c>can.db.slow_commands</c>).</summary>
    public bool RecordMetrics { get; set; } = true;

    /// <summary>Loglanan SQL'in en çok uzunluğu (karakter).</summary>
    public int MaxLoggedSqlLength { get; set; } = 2000;

    /// <summary>
    /// N+1 dedektörü: aynı SQL bir istekte (DI scope'unda) bu kadar kez çalışırsa uyarır. 0: kapalı (varsayılan).
    /// Geliştirmede açılması önerilir: <c>builder.Environment.IsDevelopment() ? 10 : 0</c>.
    /// </summary>
    public int RepeatedQueryThreshold { get; set; }

    /// <summary>Bir istekte (DI scope'unda) bundan fazla komut çalışırsa uyarır. 0: kapalı (varsayılan).</summary>
    public int MaxCommandsPerScope { get; set; }

    /// <summary>
    /// <see langword="true"/>: N+1 ya da komut sınırı aşımında <see cref="QueryDiagnosticsException"/> fırlatılır
    /// (testlerde ve geliştirmede sorunu gözden kaçırmamak için). <see langword="false"/>: yalnızca loglanır.
    /// </summary>
    public bool ThrowOnViolation { get; set; }
}

/// <summary>N+1 ya da komut sınırı aşıldı (<see cref="QueryDiagnosticsOptions.ThrowOnViolation"/> açıkken).</summary>
public sealed class QueryDiagnosticsException(string message) : InvalidOperationException(message);

/// <summary>Veritabanı komut metrikleri.</summary>
public static class QueryDiagnosticsTelemetry
{
    /// <summary>
    /// Komut süresi (saniye). Etiketler: <c>can.db.operation</c> (<c>query</c> / <c>command</c> / <c>scalar</c>),
    /// <c>can.source</c> (işlem adı, ör. <c>GetProductsQuery</c>).
    /// </summary>
    public static readonly Histogram<double> CommandDuration =
        OutboxTelemetry.Meter.CreateHistogram<double>("can.db.command.duration", unit: "s", description: "Veritabanı komutlarının süresi");

    /// <summary>Eşiği aşan komutlar. Etiketler: <c>can.db.operation</c>, <c>can.source</c>.</summary>
    public static readonly Counter<long> SlowCommands =
        OutboxTelemetry.Meter.CreateCounter<long>("can.db.slow_commands", unit: "{command}", description: "Yavaş sorgu eşiğini aşan komutlar");
}

/// <summary>
/// Yavaş sorgu takibi, sorgu etiketleme ve N+1 dedektörü. Scope başınadır (bir HTTP isteği ya da arka plan işi);
/// sayaçlar scope bitince sıfırlanır.
/// </summary>
public sealed partial class QueryDiagnosticsInterceptor : DbCommandInterceptor
{
    private const string TagPrefix = "-- can: ";

    private readonly QueryDiagnosticsOptions _options;
    private readonly ILogger<QueryDiagnosticsInterceptor> _logger;
    private Dictionary<string, int>? _executions;
    private HashSet<string>? _reported;
    private int _total;
    private bool _totalReported;

    public QueryDiagnosticsInterceptor(QueryDiagnosticsOptions options, ILogger<QueryDiagnosticsInterceptor> logger)
    {
        _options = options;
        _logger = logger;
    }

    // ---------------------------------------------------------------- çalışmadan önce: etiket

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Tag(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Tag(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Tag(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Tag(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Tag(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Tag(command);
        return ValueTask.FromResult(result);
    }

    // ---------------------------------------------------------------- çalıştıktan sonra: süre ve tekrar

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Observe(command, eventData.Duration, "query");
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData.Duration, "query");
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Observe(command, eventData.Duration, "command");
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData.Duration, "command");
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Observe(command, eventData.Duration, "scalar");
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData.Duration, "scalar");
        return ValueTask.FromResult(result);
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// İşlemin adı: mediator isteği (<see cref="OperationContext"/>), yoksa HTTP isteğinin span adı. Yalnızca güvenli
    /// karakterler kalır; SQL yorumundan çıkılamaz.
    /// </summary>
    internal static string? Source()
    {
        string? name = OperationContext.Current;
        if (name is null)
        {
            for (Activity? activity = Activity.Current; activity is not null; activity = activity.Parent)
            {
                if (activity.Kind == ActivityKind.Server)
                {
                    name = activity.DisplayName;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return null;

        var safe = new StringBuilder(Math.Min(name.Length, 100));
        foreach (char c in name.AsSpan(0, Math.Min(name.Length, 100)))
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '.' or '/' or '{' or '}' or ':' or '-' ? c : '_');
        return safe.ToString().Trim();
    }

    private void Tag(DbCommand command)
    {
        if (!_options.TagQueries || command.CommandText.StartsWith(TagPrefix, StringComparison.Ordinal) || Source() is not { Length: > 0 } source)
            return;

        command.CommandText = TagPrefix + source + "\n" + command.CommandText;
    }

    private void Observe(DbCommand command, TimeSpan duration, string operation)
    {
        string? source = null;
        if (_options.RecordMetrics && QueryDiagnosticsTelemetry.CommandDuration.Enabled)
        {
            source ??= Source() ?? "-";
            QueryDiagnosticsTelemetry.CommandDuration.Record(
                duration.TotalSeconds,
                new KeyValuePair<string, object?>("can.db.operation", operation),
                new KeyValuePair<string, object?>("can.source", source));
        }

        if (_options.SlowQueryThreshold > TimeSpan.Zero && duration >= _options.SlowQueryThreshold)
        {
            source ??= Source() ?? "-";
            if (_options.RecordMetrics)
            {
                QueryDiagnosticsTelemetry.SlowCommands.Add(
                    1,
                    new KeyValuePair<string, object?>("can.db.operation", operation),
                    new KeyValuePair<string, object?>("can.source", source));
            }

            LogSlow((long)duration.TotalMilliseconds, source, Truncate(command.CommandText));
        }

        CheckRepetition(command.CommandText);
    }

    private void CheckRepetition(string sql)
    {
        if (_options.MaxCommandsPerScope > 0 && ++_total > _options.MaxCommandsPerScope && !_totalReported)
        {
            _totalReported = true;
            string message = $"Bir istekte {_options.MaxCommandsPerScope} komut sınırı aşıldı (kaynak: {Source() ?? "-"}). Sorguları birleştir ya da sayfala.";
            LogViolation(message);
            if (_options.ThrowOnViolation)
                throw new QueryDiagnosticsException(message);
        }

        if (_options.RepeatedQueryThreshold <= 0)
            return;

        _executions ??= new Dictionary<string, int>(StringComparer.Ordinal);
        int count = _executions[sql] = _executions.GetValueOrDefault(sql) + 1;
        if (count < _options.RepeatedQueryThreshold || !(_reported ??= new HashSet<string>(StringComparer.Ordinal)).Add(sql))
            return;

        string violation = $"Olası N+1: aynı sorgu bir istekte {count} kez çalıştı (kaynak: {Source() ?? "-"}). "
            + $"Döngüdeki sorguyu Include / tek sorgu ile değiştir. SQL: {Truncate(sql)}";
        LogViolation(violation);
        if (_options.ThrowOnViolation)
            throw new QueryDiagnosticsException(violation);
    }

    private string Truncate(string sql) =>
        sql.Length <= _options.MaxLoggedSqlLength ? sql : string.Concat(sql.AsSpan(0, _options.MaxLoggedSqlLength), "…");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Yavaş sorgu ({ElapsedMs} ms, kaynak: {Source}): {Sql}")]
    private partial void LogSlow(long elapsedMs, string source, string sql);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private partial void LogViolation(string message);
}
