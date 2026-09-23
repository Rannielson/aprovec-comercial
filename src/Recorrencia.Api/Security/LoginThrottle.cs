using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Security;

/// <summary>
/// Login-failure throttle built around an atomic reserve-then-complete
/// pattern instead of check-then-act. The previous check-then-act design
/// (IsBlocked() read, followed much later by a RecordFailure() write once
/// verification actually failed) let unbounded numbers of concurrent
/// requests all pass the check before any of them recorded a failure,
/// effectively removing the throttle under parallel load.
/// </summary>
public sealed class LoginThrottle(TimeProvider time, IOptions<AuthOptions> options)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>
    /// Atomically checks, for EVERY key, whether (recorded failures within
    /// the window) + (already in-flight reservations for that key) is at or
    /// over the limit. If ANY key is at/over the limit, reserves nothing on
    /// any key and returns false. If ALL keys are under the limit, reserves
    /// a slot on every key (incrementing its in-flight counter) and returns
    /// true. Keys are locked in a consistent order (sorted) so that two
    /// calls referencing the same keys can never deadlock against each
    /// other.
    /// </summary>
    public bool TryReserve(params string[] keys)
    {
        var now = time.GetUtcNow();
        var locked = new List<(string Key, Entry Entry)>(keys.Length);
        try
        {
            foreach (var key in Ordered(keys))
            {
                var entry = LockEntry(key);
                locked.Add((key, entry));
                Prune(entry, now);
                if (entry.Failures.Count + entry.InFlight >= options.Value.MaxFailuresPerWindow)
                    return false;
            }

            foreach (var (_, entry) in locked)
                entry.InFlight++;
            return true;
        }
        finally
        {
            foreach (var (key, entry) in locked)
                ReleaseEntry(key, entry, now);
        }
    }

    /// <summary>
    /// Releases the reservation a matching <see cref="TryReserve"/> call made
    /// on every key. When <paramref name="failed"/> is true, converts the
    /// reservation into a real recorded failure (as the old RecordFailure()
    /// did); otherwise (a successful login) it is just released.
    /// </summary>
    public void Complete(string[] keys, bool failed)
    {
        var now = time.GetUtcNow();
        foreach (var key in Ordered(keys))
        {
            var entry = LockEntry(key);
            entry.InFlight = Math.Max(0, entry.InFlight - 1);
            if (failed)
                entry.Failures.Enqueue(now);
            ReleaseEntry(key, entry, now);
        }
    }

    public void Reset(string key)
    {
        var now = time.GetUtcNow();
        var entry = LockEntry(key);
        entry.Failures.Clear();
        ReleaseEntry(key, entry, now);
    }

    private static string[] Ordered(string[] keys) =>
        keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private void Prune(Entry entry, DateTimeOffset now)
    {
        var cutoff = now.AddSeconds(-options.Value.FailureWindowSeconds);
        while (entry.Failures.Count > 0 && entry.Failures.Peek() <= cutoff)
            entry.Failures.Dequeue();
    }

    /// <summary>
    /// Returns the live entry for <paramref name="key"/>, locked. This is
    /// what makes eviction in <see cref="ReleaseEntry"/> safe (unlike the
    /// bare `_failures.TryRemove(key, out _)` Task 2 had to avoid, which
    /// could orphan a queue reference a racing caller had already captured
    /// via GetOrAdd but not yet locked): every caller re-checks
    /// `entry.Retired` immediately after acquiring the lock and, if it was
    /// concurrently retired between its GetOrAdd and its Monitor.Enter,
    /// loops to fetch or create the entry that is actually live in the
    /// dictionary. No update is ever applied to an entry no longer reachable
    /// from `_entries`.
    /// </summary>
    private Entry LockEntry(string key)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry());
            Monitor.Enter(entry);
            if (!entry.Retired)
                return entry;
            Monitor.Exit(entry);
        }
    }

    /// <summary>
    /// Must be called while holding the lock obtained from <see cref="LockEntry"/>.
    /// Prunes expired failures and, if the entry is now both empty and idle
    /// (no in-flight reservations), retires and evicts it so that failed
    /// logins -- reachable by unauthenticated traffic via the login endpoint
    /// -- don't grow `_entries` without bound for as long as the process
    /// keeps running.
    /// </summary>
    private void ReleaseEntry(string key, Entry entry, DateTimeOffset now)
    {
        Prune(entry, now);
        if (entry.Failures.Count == 0 && entry.InFlight == 0)
        {
            entry.Retired = true;
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }
        Monitor.Exit(entry);
    }

    private sealed class Entry
    {
        public readonly Queue<DateTimeOffset> Failures = new();
        public int InFlight;
        public bool Retired;
    }
}
