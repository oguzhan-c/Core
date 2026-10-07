namespace Can.Core.Search.Elasticsearch;

/// <summary>
/// Küme düğümleri: istekler canlı düğümler arasında sırayla dağıtılır; ulaşılamayan (ya da 502/503/504 dönen) düğüm
/// bir süre devre dışı kalır ve istek sıradaki düğümle tekrarlanır. Devre dışı kalma süresi her başarısızlıkta artar
/// (<see cref="ElasticsearchOptions.DeadTimeout"/> → <see cref="ElasticsearchOptions.MaxDeadTimeout"/>); hiç canlı düğüm
/// kalmazsa en erken açılacak olan denenir. Elastic'in resmî istemcisindeki (Elastic.Transport) yaklaşımın sadeleştirilmiş hâli.
/// </summary>
internal sealed class ElasticsearchNodePool
{
    private readonly ElasticsearchNode[] _nodes;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _deadTimeout;
    private readonly TimeSpan _maxDeadTimeout;
    private int _cursor = -1;

    public ElasticsearchNodePool(IReadOnlyList<Uri> nodes, TimeSpan deadTimeout, TimeSpan maxDeadTimeout, TimeProvider timeProvider)
    {
        if (nodes.Count == 0)
            throw new InvalidOperationException("En az bir Elasticsearch düğümü gerekli.");

        _nodes = nodes.Select(uri => new ElasticsearchNode(uri)).ToArray();
        _deadTimeout = deadTimeout;
        _maxDeadTimeout = maxDeadTimeout;
        _timeProvider = timeProvider;
    }

    public IReadOnlyList<ElasticsearchNode> Nodes => _nodes;

    /// <summary>Bu istek için denenecek düğümler (sırası her istekte kayar).</summary>
    public IEnumerable<ElasticsearchNode> CreateView()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        ElasticsearchNode[] alive = _nodes.Where(n => n.IsAvailable(now)).ToArray();
        int start = Interlocked.Increment(ref _cursor) & int.MaxValue;

        if (alive.Length == 0)
        {
            // Hepsi devre dışı: en erken açılacak olanı dene (küme toparlanmış olabilir).
            yield return _nodes.MinBy(n => n.DeadUntil)!;
            yield break;
        }

        for (int i = 0; i < alive.Length; i++)
            yield return alive[(start + i) % alive.Length];
    }

    public void MarkDead(ElasticsearchNode node)
    {
        int failures = node.RegisterFailure();
        // Elastic.Transport ile aynı eğri: 60 sn, ~85 sn, 120 sn ... en çok 30 dk.
        double milliseconds = Math.Min(_deadTimeout.TotalMilliseconds * 2 * Math.Pow(2, (failures * 0.5) - 1), _maxDeadTimeout.TotalMilliseconds);
        node.DeadUntil = _timeProvider.GetUtcNow().AddMilliseconds(milliseconds);
    }

    public static void MarkAlive(ElasticsearchNode node) => node.Reset();
}

internal sealed class ElasticsearchNode(Uri uri)
{
    private int _failures;
    private long _deadUntilTicks;

    public Uri Uri { get; } = uri;

    public int Failures => Volatile.Read(ref _failures);

    public DateTimeOffset DeadUntil
    {
        get => new(Volatile.Read(ref _deadUntilTicks), TimeSpan.Zero);
        set => Volatile.Write(ref _deadUntilTicks, value.UtcTicks);
    }

    public bool IsAvailable(DateTimeOffset now) => Failures == 0 || DeadUntil <= now;

    public int RegisterFailure() => Interlocked.Increment(ref _failures);

    public void Reset()
    {
        Volatile.Write(ref _failures, 0);
        Volatile.Write(ref _deadUntilTicks, 0);
    }
}
