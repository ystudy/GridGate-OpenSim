using System.Net;

namespace GridGate.Launcher.Economy;

/// <summary>
/// What keeps the economy helper from flooding the shared database (money design §8.5): <see cref="PerMinute"/> calls over a
/// rolling minute for one address, <see cref="PerSecond"/> calls over a rolling second for all addresses together, at most
/// <see cref="MaxAddresses"/> addresses tracked, and at most <see cref="MaxLookups"/> session lookups at once. In memory only;
/// an IPv4-mapped IPv6 address counts as its IPv4 address, and an unknown address counts nothing. Safe from any thread.
/// </summary>
public sealed class EconomyLimits(TimeProvider time, int maxAddresses = EconomyLimits.MaxAddresses,
    int perSecond = EconomyLimits.PerSecond, int maxLookups = EconomyLimits.MaxLookups)
{
    /// <summary>Calls one address may make over a rolling minute.</summary>
    public const int PerMinute = 60;

    /// <summary>Calls all addresses together may make over a rolling second; only calls that passed their address's limit count.</summary>
    public const int PerSecond = 20;

    /// <summary>Addresses tracked. When the table is full after a due prune, a new address is refused.</summary>
    public const int MaxAddresses = 10_000;

    /// <summary>Session lookups at once.</summary>
    public const int MaxLookups = 8;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _calls = new();
    private readonly Queue<DateTimeOffset> _all = new();
    private DateTimeOffset _pruned = DateTimeOffset.MinValue;
    private int _lookups;

    /// <summary>Counts this call and says whether it is within the limits.</summary>
    public bool Allow(IPAddress? ip)
    {
        if (ip is null) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_calls.TryGetValue(ip, out var times))
            {
                // Old addresses are dropped at most once a minute, so many addresses can't make every call walk the table.
                if (_calls.Count >= maxAddresses && now - _pruned >= Window) Prune(now);
                if (_calls.Count >= maxAddresses) return false;
                times = new Queue<DateTimeOffset>();
            }
            while (times.Count > 0 && now - times.Peek() >= Window) times.Dequeue();
            if (times.Count >= PerMinute) return false;
            while (_all.Count > 0 && now - _all.Peek() >= Second) _all.Dequeue();
            if (_all.Count >= perSecond) return false;
            times.Enqueue(now);
            _all.Enqueue(now);
            _calls[ip] = times;
            return true;
        }
    }

    /// <summary>Takes one of the session-lookup slots; false when they are all in use. Every true needs an <see cref="EndLookup"/>.</summary>
    public bool TryStartLookup()
    {
        if (Interlocked.Increment(ref _lookups) <= maxLookups) return true;
        Interlocked.Decrement(ref _lookups);
        return false;
    }

    public void EndLookup() => Interlocked.Decrement(ref _lookups);

    private void Prune(DateTimeOffset now)
    {
        _pruned = now;
        foreach (var (ip, times) in _calls.ToList())
            if (times.Count == 0 || now - times.Last() >= Window)
                _calls.Remove(ip);
    }
}
