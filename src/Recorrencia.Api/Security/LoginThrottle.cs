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
            if (queue.Count == 0)
                _failures.TryRemove(key, out _);
            return queue.Count;
        }
    }
}
