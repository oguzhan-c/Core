using System.Collections.Concurrent;
using System.Text.Json;

namespace Can.Core.Sms;

/// <summary>Testler için: gönderilen SMS'leri bellekte tutar (gerçekten göndermez).</summary>
public sealed class InMemorySmsProvider : ISmsProvider
{
    private readonly ConcurrentQueue<SmsRequest> _messages = new();

    public string Name => "in-memory";

    public IReadOnlyCollection<SmsRequest> Messages => _messages.ToArray();

    /// <summary>Bu numaraya giden son mesaj (testlerde doğrulama kodunu okumak için).</summary>
    public SmsRequest? LastTo(string e164) => _messages.LastOrDefault(m => m.To == e164);

    public void Clear() => _messages.Clear();

    public Task<SmsSendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _messages.Enqueue(request);
        return Task.FromResult(SmsSendResult.Sent(Guid.NewGuid().ToString("N"), request.Segments));
    }
}

/// <summary>
/// Geliştirme için: her SMS'i klasöre bir JSON dosyası olarak yazar (gerçekten göndermez). Doğrulama kodlarını
/// buradan okuyabilirsin. Canlıda kullanma: mesajlar (kodlar) diske düz metin yazılır.
/// </summary>
public sealed class PickupDirectorySmsProvider : ISmsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;

    public PickupDirectorySmsProvider(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => "pickup";

    public async Task<SmsSendResult> SendAsync(SmsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(_directory);

        string id = Guid.CreateVersion7().ToString("N");
        DateTimeOffset now = _timeProvider.GetUtcNow();
        string path = Path.Combine(_directory, $"{now:yyyyMMdd-HHmmss}-{id}.sms.json");

        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, new { id, date = now, request.To, request.From, request.Text, request.Encoding, request.Segments, request.Reference }, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return SmsSendResult.Sent(id, request.Segments);
    }
}
