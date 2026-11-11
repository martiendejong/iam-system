using IAM.Core.Services;

namespace IAM.Infrastructure.Services;

public class InMemoryRequestThrottle : IRequestThrottle
{
    private const int PruneThreshold = 10_000;
    private const int MaxKeyLength = 320;

    private readonly Dictionary<string, Queue<DateTime>> _hits = new();
    private readonly object _lock = new();

    public bool TryAcquire(string key, int limit, TimeSpan window)
    {
        if (key.Length > MaxKeyLength) key = key[..MaxKeyLength];
        var now = DateTime.UtcNow;
        var cutoff = now - window;

        lock (_lock)
        {
            if (_hits.Count > PruneThreshold) Prune(cutoff);

            if (!_hits.TryGetValue(key, out var queue))
            {
                queue = new Queue<DateTime>();
                _hits[key] = queue;
            }

            while (queue.Count > 0 && queue.Peek() < cutoff) queue.Dequeue();

            if (queue.Count >= limit) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    private void Prune(DateTime cutoff)
    {
        var stale = _hits.Where(kv => kv.Value.Count == 0 || kv.Value.Last() < cutoff)
            .Select(kv => kv.Key).ToList();
        foreach (var key in stale) _hits.Remove(key);
    }
}
