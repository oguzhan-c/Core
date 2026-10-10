using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Can.Core.Application;
using Can.Core.MultiTenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Can.Core.WebApi.Idempotency;

/// <summary>
/// <c>Idempotency-Key</c> (IETF taslağı) ile tekrar eden istekleri bir kez işler:
/// <list type="bullet">
/// <item>Aynı anahtar + aynı istek: ilk yanıt (durum, gövde, <see cref="CanIdempotencyOptions.ReplayedHeaders"/>) aynen
/// döner, <c>Idempotent-Replayed: true</c> eklenir; handler çalışmaz.</item>
/// <item>Aynı anahtar + farklı istek (yöntem, yol, sorgu ya da gövde): 422 <c>idempotency_key_reused</c>.</item>
/// <item>Aynı anahtarla istek hâlâ işleniyor: 409 <c>idempotency_in_progress</c> (+ <c>Retry-After</c>).</item>
/// <item>Anahtar zorunlu ama yok: 400 <c>idempotency_key_required</c>.</item>
/// </list>
/// Anahtar tenant + kullanıcı ile ayrılır: başkasının anahtarıyla onun yanıtı alınamaz. 5xx, 408, 429 ve
/// 401/403 yanıtları saklanmaz (istemci tekrar deneyebilsin).
/// </summary>
internal sealed partial class IdempotencyMiddleware
{
    private const string ReplayedHeader = "Idempotent-Replayed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly CanIdempotencyOptions _options;
    private readonly IDistributedCache _cache;
    private readonly LocalIdempotencyLocks _localLocks;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(RequestDelegate next, CanIdempotencyOptions options, IDistributedCache cache, LocalIdempotencyLocks localLocks, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _options = options;
        _cache = cache;
        _localLocks = localLocks;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IdempotentAttribute>() is not { } idempotent)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        StringValues values = context.Request.Headers[_options.HeaderName];
        if (StringValues.IsNullOrEmpty(values))
        {
            if (idempotent.Required)
                await ProblemAsync(context, StatusCodes.Status400BadRequest, ProblemTitles.Validation, "idempotency_key_required", $"'{_options.HeaderName}' başlığı gerekli.").ConfigureAwait(false);
            else
                await _next(context).ConfigureAwait(false);
            return;
        }

        string key = values.ToString();
        if (values.Count > 1 || key.Length > _options.MaxKeyLength || key.Any(char.IsControl))
        {
            await ProblemAsync(context, StatusCodes.Status400BadRequest, ProblemTitles.Validation, "idempotency_key_invalid", $"'{_options.HeaderName}' geçersiz (tek değer, en çok {_options.MaxKeyLength} karakter).").ConfigureAwait(false);
            return;
        }

        CancellationToken aborted = context.RequestAborted;
        string cacheKey = CacheKey(context, key);
        string fingerprint = await FingerprintAsync(context.Request, aborted).ConfigureAwait(false);

        if (await TryReplayAsync(context, cacheKey, fingerprint, aborted).ConfigureAwait(false))
            return;

        IDistributedLock locks = context.RequestServices.GetService<IDistributedLock>() ?? _localLocks;
        IAsyncDisposable? handle = await locks.TryAcquireAsync(cacheKey + ":lock", _options.LockTimeout, cancellationToken: aborted).ConfigureAwait(false);
        if (handle is null)
        {
            context.Response.Headers.RetryAfter = "1";
            await ProblemAsync(context, StatusCodes.Status409Conflict, ProblemTitles.Conflict, "idempotency_in_progress", "Bu anahtarla gönderilen istek hâlâ işleniyor.").ConfigureAwait(false);
            return;
        }

        await using (handle.ConfigureAwait(false))
        {
            // kilidi beklerken diğer istek bitirmiş olabilir
            if (await TryReplayAsync(context, cacheKey, fingerprint, aborted).ConfigureAwait(false))
                return;

            Stream original = context.Response.Body;
            using var capture = new CaptureStream(original, _options.MaxResponseBodySize);
            context.Response.Body = capture;
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = original;
            }

            int status = context.Response.StatusCode;
            if (!ShouldStore(status))
                return;
            if (capture.Overflowed)
            {
                LogTooLarge(context.Request.Path, _options.MaxResponseBodySize);
                return;
            }

            var stored = new StoredResponse(
                fingerprint,
                status,
                context.Response.ContentType,
                _options.ReplayedHeaders
                    .Where(h => context.Response.Headers.ContainsKey(h))
                    .ToDictionary(h => h, h => context.Response.Headers[h].Where(v => v is not null).Select(v => v!).ToArray(), StringComparer.OrdinalIgnoreCase),
                capture.Captured);
            await _cache.SetAsync(
                cacheKey,
                JsonSerializer.SerializeToUtf8Bytes(stored, Json),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.Expiration },
                CancellationToken.None).ConfigureAwait(false); // yanıt gitti: istemci kopsa da saklansın
        }
    }

    private async Task<bool> TryReplayAsync(HttpContext context, string cacheKey, string fingerprint, CancellationToken cancellationToken)
    {
        byte[]? bytes = await _cache.GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (bytes is null || JsonSerializer.Deserialize<StoredResponse>(bytes, Json) is not { } stored)
            return false;

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored.Fingerprint), Encoding.ASCII.GetBytes(fingerprint)))
        {
            await ProblemAsync(context, StatusCodes.Status422UnprocessableEntity, ProblemTitles.UnprocessableContent, "idempotency_key_reused", "Bu anahtar farklı bir istekle kullanılmış.").ConfigureAwait(false);
            return true;
        }

        LogReplayed(context.Request.Method, context.Request.Path);
        HttpResponse response = context.Response;
        response.StatusCode = stored.Status;
        if (stored.ContentType is not null)
            response.ContentType = stored.ContentType;
        foreach ((string name, string[] value) in stored.Headers)
            response.Headers[name] = value;
        response.Headers[ReplayedHeader] = "true";
        if (stored.Body.Length > 0)
            await response.Body.WriteAsync(stored.Body, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Anahtar tenant + kullanıcı ile ayrılır; yöntem ve yol parmak izindedir (yanlış uç noktaya tekrar 422 alır).</summary>
    private static string CacheKey(HttpContext context, string key)
    {
        string tenant = context.RequestServices.GetService<TenantContext>()?.TenantId ?? "-";
        string user = context.RequestServices.GetService<ICurrentUser>()?.Id ?? "-";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}\n{user}\n{key}"));
        return "can:idempotency:" + Convert.ToHexStringLower(hash);
    }

    private static async Task<string> FingerprintAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}\n{request.QueryString}\n"));

        request.EnableBuffering(); // gövde handler için yeniden okunabilsin
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            request.Body.Position = 0;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool ShouldStore(int status) =>
        status < StatusCodes.Status500InternalServerError
        && status is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status408RequestTimeout or StatusCodes.Status429TooManyRequests);

    private static async Task ProblemAsync(HttpContext context, int status, string title, string code, string detail)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions["code"] = code;
        context.Response.StatusCode = status;

        IProblemDetailsService? problemDetails = context.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetails is null || !await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }).ConfigureAwait(false))
            await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Idempotency: {Method} {Path} için saklanan yanıt döndü.")]
    private partial void LogReplayed(string method, PathString path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idempotency: {Path} yanıtı {Limit} bayttan büyük, saklanmadı.")]
    private partial void LogTooLarge(PathString path, int limit);

    private sealed record StoredResponse(string Fingerprint, int Status, string? ContentType, Dictionary<string, string[]> Headers, byte[] Body);

    /// <summary>Yazılanı asıl akışa geçirir, sınıra kadar kopyasını tutar.</summary>
    private sealed class CaptureStream(Stream inner, int limit) : Stream
    {
        private readonly MemoryStream _copy = new();

        public bool Overflowed { get; private set; }

        public byte[] Captured => _copy.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Copy(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Copy(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Copy(ReadOnlySpan<byte> data)
        {
            if (Overflowed)
                return;
            if (_copy.Length + data.Length > limit)
            {
                Overflowed = true;
                _copy.SetLength(0);
                return;
            }

            _copy.Write(data);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _copy.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary><see cref="IDistributedLock"/> kayıtlı değilse süreç içi kilit (tek sunucu).</summary>
internal sealed class LocalIdempotencyLocks : IDistributedLock
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    public Task<IAsyncDisposable?> TryAcquireAsync(string resource, TimeSpan expiry, TimeSpan wait = default, CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsyncDisposable?>(_held.TryAdd(resource, 0) ? new Release(_held, resource) : null);

    private sealed class Release(ConcurrentDictionary<string, byte> held, string resource) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            held.TryRemove(resource, out _);
            return ValueTask.CompletedTask;
        }
    }
}
