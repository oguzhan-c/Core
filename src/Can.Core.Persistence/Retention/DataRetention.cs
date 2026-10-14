using System.Diagnostics.Metrics;
using System.Linq.Expressions;
using Can.Core.Application;
using Can.Core.BackgroundJobs;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Can.Core.Persistence.Retention;

/// <summary>
/// <c>AddCanDataRetention(...)</c> ayarları. Hiçbir tablo kendiliğinden temizlenmez: yalnızca <c>Keep</c> ile kural
/// verilen tablolar, verilen süreden eski kayıtları için silinir.
/// </summary>
public sealed class DataRetentionOptions
{
    private readonly List<RetentionRule> _rules = [];

    /// <summary>Temizliğin çalışma aralığı.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Tek silme komutundaki en çok kayıt (kilitleri ve transaction log'unu küçük tutar).</summary>
    public int BatchSize { get; set; } = 5000;

    /// <summary>Bir çalışmada kural başına en çok parti; kalanlar sonraki çalışmada silinir (ilk açılışta uzun sürmesin).</summary>
    public int MaxBatchesPerRun { get; set; } = 100;

    /// <summary>
    /// <see langword="true"/>: hiçbir şey silinmez, yalnızca kaç kaydın silineceği loglanır. İlk kez açarken kuralları
    /// doğrulamak için.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>Tenant başına veritabanı: temizlik her tenant'ın veritabanında ayrı çalışır.</summary>
    public bool PerTenantDatabases { get; set; }

    public IReadOnlyList<RetentionRule> Rules => _rules;

    /// <summary><typeparamref name="TEntity"/> kayıtları <paramref name="timestamp"/>'ten <paramref name="period"/> sonra silinir.</summary>
    /// <param name="period">Saklama süresi.</param>
    /// <param name="timestamp">Kaydın zamanı (ör. <c>a =&gt; a.Timestamp</c>).</param>
    /// <param name="where">Ek koşul (ör. yalnızca iptal edilmişler); boşsa tümü.</param>
    public DataRetentionOptions Keep<TEntity>(TimeSpan period, Expression<Func<TEntity, DateTimeOffset>> timestamp, Expression<Func<TEntity, bool>>? where = null)
        where TEntity : class => Add(new RetentionRule<TEntity>(period, timestamp, where, utcDateTime: false));

    /// <summary><typeparamref name="TEntity"/> kayıtları zamanından <paramref name="period"/> sonra silinir.</summary>
    /// <param name="period">Saklama süresi.</param>
    /// <param name="timestamp">Kaydın zamanı.</param>
    /// <param name="where">Ek koşul; boşsa tümü.</param>
    /// <remarks>Zaman boş (<see langword="null"/>) olan kayıtlar silinmez (ör. henüz işlenmemiş).</remarks>
    public DataRetentionOptions Keep<TEntity>(TimeSpan period, Expression<Func<TEntity, DateTimeOffset?>> timestamp, Expression<Func<TEntity, bool>>? where = null)
        where TEntity : class => Add(new RetentionRule<TEntity>(period, timestamp, where, utcDateTime: false));

    /// <summary><typeparamref name="TEntity"/> kayıtları zamanından <paramref name="period"/> sonra silinir.</summary>
    /// <param name="period">Saklama süresi.</param>
    /// <param name="timestamp">Kaydın zamanı.</param>
    /// <param name="where">Ek koşul; boşsa tümü.</param>
    /// <remarks>Zaman UTC <see cref="DateTime"/> olmalı.</remarks>
    public DataRetentionOptions Keep<TEntity>(TimeSpan period, Expression<Func<TEntity, DateTime>> timestamp, Expression<Func<TEntity, bool>>? where = null)
        where TEntity : class => Add(new RetentionRule<TEntity>(period, timestamp, where, utcDateTime: true));

    /// <summary><typeparamref name="TEntity"/> kayıtları zamanından <paramref name="period"/> sonra silinir.</summary>
    /// <param name="period">Saklama süresi.</param>
    /// <param name="timestamp">Kaydın zamanı.</param>
    /// <param name="where">Ek koşul; boşsa tümü.</param>
    /// <remarks>Zaman UTC <see cref="DateTime"/> olmalı; boş olan kayıtlar silinmez.</remarks>
    public DataRetentionOptions Keep<TEntity>(TimeSpan period, Expression<Func<TEntity, DateTime?>> timestamp, Expression<Func<TEntity, bool>>? where = null)
        where TEntity : class => Add(new RetentionRule<TEntity>(period, timestamp, where, utcDateTime: true));

    private DataRetentionOptions Add(RetentionRule rule)
    {
        _rules.Add(rule);
        return this;
    }

    internal void Validate()
    {
        if (_rules.Count == 0)
            throw new InvalidOperationException("AddCanDataRetention: en az bir kural ver (o.Keep<TEntity>(süre, x => x.Zaman)).");
        if (Interval <= TimeSpan.Zero)
            throw new InvalidOperationException("DataRetentionOptions.Interval sıfırdan büyük olmalı.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize, nameof(BatchSize));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBatchesPerRun, nameof(MaxBatchesPerRun));
    }
}

/// <summary>Bir tablonun saklama kuralı.</summary>
public abstract class RetentionRule
{
    protected RetentionRule(string name, TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), period, "Saklama süresi sıfırdan büyük olmalı.");
        Name = name;
        Period = period;
    }

    /// <summary>Log ve metrikteki adı (entity tipi).</summary>
    public string Name { get; }

    public TimeSpan Period { get; }

    /// <summary>Süresi dolan kayıtları siler (ya da sayar); silinen sayısını döner.</summary>
    internal abstract Task<int> ApplyAsync(DbContext context, DateTimeOffset now, DataRetentionOptions options, CancellationToken cancellationToken);
}

internal sealed class RetentionRule<TEntity> : RetentionRule
    where TEntity : class
{
    private readonly LambdaExpression _timestamp;
    private readonly Expression<Func<TEntity, bool>>? _where;
    private readonly bool _utcDateTime;

    public RetentionRule(TimeSpan period, LambdaExpression timestamp, Expression<Func<TEntity, bool>>? where, bool utcDateTime)
        : base(typeof(TEntity).Name, period)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        _timestamp = timestamp;
        _where = where;
        _utcDateTime = utcDateTime;
    }

    internal override async Task<int> ApplyAsync(DbContext context, DateTimeOffset now, DataRetentionOptions options, CancellationToken cancellationToken)
    {
        // Silme tenant ve soft delete filtrelerinden bağımsızdır: süresi dolan kayıt kimin olursa olsun gider.
        IQueryable<TEntity> expired = context.Set<TEntity>().IgnoreQueryFilters().Where(OlderThan(now - Period));
        if (_where is not null)
            expired = expired.Where(_where);

        if (options.DryRun)
            return await expired.CountAsync(cancellationToken).ConfigureAwait(false);

        int total = 0;
        for (int batch = 0; batch < options.MaxBatchesPerRun; batch++)
        {
            int deleted = await expired.Take(options.BatchSize).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < options.BatchSize)
                break;
        }

        return total;
    }

    /// <summary><c>x =&gt; x.Zaman &lt; sınır</c>; sınır sabit değil parametre olarak gider.</summary>
    private Expression<Func<TEntity, bool>> OlderThan(DateTimeOffset cutoff)
    {
        ParameterExpression parameter = _timestamp.Parameters[0];
        Expression value = _utcDateTime
            ? Expression.Property(Expression.Constant(new Holder<DateTime>(cutoff.UtcDateTime)), nameof(Holder<DateTime>.Value))
            : Expression.Property(Expression.Constant(new Holder<DateTimeOffset>(cutoff)), nameof(Holder<DateTimeOffset>.Value));

        Expression body = _timestamp.Body;
        if (Nullable.GetUnderlyingType(body.Type) is not null)
            value = Expression.Convert(value, body.Type); // null < x → false: zamanı boş olan kayıt silinmez

        return Expression.Lambda<Func<TEntity, bool>>(Expression.LessThan(body, value), parameter);
    }

    private sealed class Holder<T>(T value)
    {
        public T Value { get; } = value;
    }
}

/// <summary>Kuralları uygulayan arka plan işi. Birden çok sunucuda aynı anda yalnızca biri çalışır (<see cref="IDistributedLock"/> kayıtlıysa).</summary>
public sealed partial class DataRetentionJob<TContext> : IBackgroundJob
    where TContext : DbContext
{
    private static readonly Counter<long> Deleted =
        OutboxTelemetry.Meter.CreateCounter<long>("can.retention.deleted", unit: "{record}", description: "Saklama süresi dolduğu için silinen kayıtlar");

    private readonly TContext _context;
    private readonly DataRetentionSettings<TContext> _settings;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceProvider _services;
    private readonly ILogger<DataRetentionJob<TContext>> _logger;

    public DataRetentionJob(TContext context, DataRetentionSettings<TContext> settings, TimeProvider timeProvider, IServiceProvider services, ILogger<DataRetentionJob<TContext>> logger)
    {
        _context = context;
        _settings = settings;
        _timeProvider = timeProvider;
        _services = services;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        DataRetentionOptions options = _settings.Options;
        string tenant = _services.GetService<TenantContext>()?.TenantId ?? "-";

        IAsyncDisposable? handle = null;
        if (_services.GetService<IDistributedLock>() is { } locks)
        {
            handle = await locks.TryAcquireAsync($"can:retention:{typeof(TContext).FullName}:{tenant}", TimeSpan.FromMinutes(30), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (handle is null)
                return; // başka bir sunucu çalıştırıyor
        }

        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (RetentionRule rule in options.Rules)
            {
                try
                {
                    int count = await rule.ApplyAsync(_context, now, options, cancellationToken).ConfigureAwait(false);
                    if (options.DryRun)
                    {
                        LogDryRun(rule.Name, count, rule.Period.TotalDays, tenant);
                    }
                    else if (count > 0)
                    {
                        Deleted.Add(count, new KeyValuePair<string, object?>("can.retention.rule", rule.Name));
                        LogDeleted(rule.Name, count, rule.Period.TotalDays, tenant);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Bir tablonun hatası diğerlerini durdurmasın.
                    LogFailed(exception, rule.Name, tenant);
                }
            }
        }
        finally
        {
            if (handle is not null)
                await handle.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Veri saklama: {Rule} tablosundan {Count} kayıt silindi ({Days} günden eski, tenant {Tenant}).")]
    private partial void LogDeleted(string rule, int count, double days, string tenant);

    [LoggerMessage(Level = LogLevel.Information, Message = "Veri saklama (deneme): {Rule} tablosundan {Count} kayıt silinecekti ({Days} günden eski, tenant {Tenant}). Hiçbir şey silinmedi.")]
    private partial void LogDryRun(string rule, int count, double days, string tenant);

    [LoggerMessage(Level = LogLevel.Error, Message = "Veri saklama: {Rule} temizlenemedi (tenant {Tenant}).")]
    private partial void LogFailed(Exception exception, string rule, string tenant);
}

/// <summary><typeparamref name="TContext"/>'e ait saklama ayarları (birden çok DbContext ayrı kurallarla kullanılabilir).</summary>
public sealed class DataRetentionSettings<TContext>(DataRetentionOptions options)
    where TContext : DbContext
{
    public DataRetentionOptions Options { get; } = options;
}

public static class DataRetentionServiceCollectionExtensions
{
    /// <summary>
    /// Veri saklama ve temizlik. <b>Varsayılan olarak hiçbir şey silinmez</b>: yalnızca kural verilen tablolarda, verilen
    /// süreden eski kayıtlar düzenli aralıklarla silinir. Kayıtların tamamı saklanmak isteniyorsa bu çağrılmaz.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanDataRetention&lt;AppDbContext&gt;(o =&gt;
    /// {
    ///     o.Keep&lt;AuditLog&gt;(TimeSpan.FromDays(365), a =&gt; a.Timestamp);
    ///     o.Keep&lt;SecurityEventRecord&gt;(TimeSpan.FromDays(180), e =&gt; e.OccurredAt);
    ///     o.Keep&lt;RefreshToken&lt;Guid&gt;&gt;(TimeSpan.FromDays(30), t =&gt; t.ExpiresAt);
    ///     o.DryRun = true; // önce yalnızca say
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanDataRetention<TContext>(this IServiceCollection services, Action<DataRetentionOptions> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new DataRetentionOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<DataRetentionSettings<TContext>>();
        services.AddSingleton(new DataRetentionSettings<TContext>(options));
        services.AddCanRecurringJob<DataRetentionJob<TContext>>(o =>
        {
            o.Interval = options.Interval;
            o.RunOnStartup = false; // açılışı yavaşlatmasın
            o.PerTenant = options.PerTenantDatabases;
        });
        return services;
    }
}
