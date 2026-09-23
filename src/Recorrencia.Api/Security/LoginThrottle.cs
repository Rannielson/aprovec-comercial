using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Security;

public sealed class LoginThrottle(TimeProvider time, IOptions<AuthOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();

    public bool IsBlocked(params string[] keys)
    {
        var now = time.GetUtcNow();
        return keys.Any(key => Count(key, now) >= options.Value.MaxFailuresPerWindow);
    }

    public void RecordFailure(params string[] keys)
    {
        var now = time.GetUtcNow();
        foreach (var key in keys)
        {
            var queue = _failures.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
            lock (queue)
                queue.Enqueue(now);
        }
    }

    public void Reset(string key) => _failures.TryRemove(key, out _);

    private int Count(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var queue))
            return 0;
        var cutoff = now.AddSeconds(-options.Value.FailureWindowSeconds);
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= cutoff)
                queue.Dequeue();
            // Deliberately not removing the (now possibly empty) queue from
            // _failures here: doing so by key alone raced with a concurrent
            // RecordFailure that had already looked up this same queue via
            // GetOrAdd but not yet taken the lock, silently dropping its
            // failure once it enqueued into an orphaned queue. An empty
            // queue left in the dictionary is harmless — a future
            // RecordFailure for this key just reuses it via GetOrAdd.
            return queue.Count;
        }
    }
}
