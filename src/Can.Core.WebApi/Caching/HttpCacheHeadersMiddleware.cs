using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Can.Core.WebApi.Caching;

/// <summary>
/// GET/HEAD yanıtları için:
/// <list type="bullet">
/// <item>ETag: 200 yanıtının gövdesinin özeti (zayıf ETag, sıkıştırmadan bağımsız). Uç nokta kendi ETag'ini verdiyse
/// (ör. satır sürümü) o kullanılır.</item>
/// <item>İstek <c>If-None-Match</c> ile aynı ETag'i gönderdiyse gövdesiz 304 döner.</item>
/// <item><c>Cache-Control</c> yoksa güvenli varsayılan; <see cref="NoStoreResponseAttribute"/> varsa <c>no-store</c>.</item>
/// </list>
/// Gövde <see cref="CanHttpCachingOptions.MaximumETagBodySize"/>'a kadar bellekte tutulur; daha büyük yanıtlar ve
/// <c>text/event-stream</c> ETag'siz, akış olarak gönderilir.
/// </summary>
internal sealed class HttpCacheHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CanHttpCachingOptions _options;

    public HttpCacheHeadersMiddleware(RequestDelegate next, CanHttpCachingOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        if (!(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<NoStoreResponseAttribute>() is not null)
        {
            context.Response.OnStarting(static state =>
            {
                IHeaderDictionary headers = ((HttpResponse)state).Headers;
                headers.CacheControl = "no-store";
                headers.Remove(HeaderNames.ETag);
                return Task.CompletedTask;
            }, context.Response);
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!_options.UseETags)
        {
            context.Response.OnStarting(static state =>
            {
                ApplyDefaultCacheControl((HttpContext)state);
                return Task.CompletedTask;
            }, context);
            await _next(context).ConfigureAwait(false);
            return;
        }

        HttpResponse response = context.Response;
        Stream original = response.Body;
        await using var buffer = new BufferingStream(original, _options.MaximumETagBodySize, context, this);
        response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = original;
        }

        if (!buffer.IsBuffering)
            return; // büyük ya da akış yanıtı: zaten gönderildi

        ReadOnlyMemory<byte> body = buffer.Buffered;
        if (response.StatusCode == StatusCodes.Status200OK)
        {
            if (StringValues.IsNullOrEmpty(response.Headers.ETag) && body.Length > 0)
                response.Headers.ETag = ComputeETag(body.Span);
            ApplyDefaultCacheControl(context);

            if (IsNotModified(request, response))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                response.ContentLength = null;
                response.Headers.ContentType = StringValues.Empty;
                return;
            }

            response.ContentLength = body.Length;
        }

        if (body.Length > 0)
            await original.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Akışa geçmeden önce (başlıklar gitmeden) çağrılır.</summary>
    internal void BeforeStreaming(HttpContext context)
    {
        if (context.Response.StatusCode == StatusCodes.Status200OK)
            ApplyDefaultCacheControl(context);
    }

    private static void ApplyDefaultCacheControl(HttpContext context)
    {
        HttpResponse response = context.Response;
        if (response.StatusCode != StatusCodes.Status200OK || !StringValues.IsNullOrEmpty(response.Headers.CacheControl))
            return;
        if (context.RequestServices.GetService(typeof(CanHttpCachingOptions)) is CanHttpCachingOptions { SetDefaultCacheControl: false })
            return;

        // private: paylaşılan proxy'ler (CDN, kurum proxy'si) saklamaz. no-cache: tarayıcı saklar ama her seferinde
        // ETag ile doğrular (değişmediyse 304, gövde inmez).
        response.Headers.CacheControl = context.User.Identity?.IsAuthenticated == true ? "private, no-cache" : "no-cache";
    }

    private static string ComputeETag(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, hash);
        return "W/\"" + Convert.ToHexStringLower(hash[..16]) + "\"";
    }

    private static bool IsNotModified(HttpRequest request, HttpResponse response)
    {
        StringValues ifNoneMatch = request.Headers.IfNoneMatch;
        if (StringValues.IsNullOrEmpty(ifNoneMatch)
            || !EntityTagHeaderValue.TryParse(response.Headers.ETag.ToString(), out EntityTagHeaderValue? current)
            || !EntityTagHeaderValue.TryParseList(ifNoneMatch.Where(v => v is not null).Select(v => v!).ToList(), out IList<EntityTagHeaderValue>? candidates))
        {
            return false;
        }

        // If-None-Match zayıf karşılaştırma kullanır (RFC 9110 13.1.2)
        return candidates.Any(c => c.Equals(EntityTagHeaderValue.Any) || c.Compare(current!, useStrongComparison: false));
    }

    /// <summary>Sınıra kadar bellekte tutar; aşılırsa (ya da olay akışıysa) biriktirileni yazıp akışa geçer.</summary>
    private sealed class BufferingStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _limit;
        private readonly HttpContext _context;
        private readonly HttpCacheHeadersMiddleware _owner;
        private MemoryStream? _buffer = new();

        public BufferingStream(Stream inner, int limit, HttpContext context, HttpCacheHeadersMiddleware owner)
        {
            _inner = inner;
            _limit = limit;
            _context = context;
            _owner = owner;
        }

        public bool IsBuffering => _buffer is not null;

        public ReadOnlyMemory<byte> Buffered => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.GetBuffer().AsMemory(0, (int)_buffer.Length);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_buffer is not null)
            {
                if (_buffer.Length + buffer.Length <= _limit && !IsEventStream())
                {
                    _buffer.Write(buffer.Span);
                    return;
                }

                await SwitchToStreamingAsync(cancellationToken).ConfigureAwait(false);
            }

            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        // JSON yazıcıları her yanıtın sonunda flush eder: biriktirirken flush yok sayılır (yoksa hiç ETag üretilemezdi).
        public override void Flush()
        {
            if (_buffer is null)
                _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _buffer is null ? _inner.FlushAsync(cancellationToken) : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _buffer?.Dispose();
            base.Dispose(disposing);
        }

        private bool IsEventStream() =>
            _context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

        private async Task SwitchToStreamingAsync(CancellationToken cancellationToken)
        {
            MemoryStream buffered = _buffer!;
            _buffer = null;
            _owner.BeforeStreaming(_context);
            if (buffered.Length > 0)
                await _inner.WriteAsync(buffered.GetBuffer().AsMemory(0, (int)buffered.Length), cancellationToken).ConfigureAwait(false);
            await buffered.DisposeAsync().ConfigureAwait(false);
        }
    }
}
