using System.Net;

namespace GridGate.Launcher.Economy;

/// <summary>
/// What keeps the economy helper from flooding the shared database (money design §8.5): <see cref="PerMinute"/> calls over a
/// rolling minute for one address, <see cref="PerSecond"/> session lookups over a rolling second for all addresses together
/// (see <see cref="TryTakeLookupSecond"/>), at most <see cref="MaxAddresses"/> addresses tracked, and at most
/// <see cref="MaxLookups"/> session lookups at once. A session that was looked up and found good is remembered for
/// <see cref="CheckedFor"/> (and again from each use), so a viewer in use does not compete for the lookup limits. An address whose
/// lookups keep finding no session gets only <see cref="MissesPerMinute"/> of them a minute, and a pair that found no session is
/// remembered as unknown for <see cref="UnknownFor"/>, so a few addresses sending fresh ids can't use up the lookups. In memory only;
/// an IPv4-mapped IPv6 address counts as its IPv4 address, and an unknown address counts against the grid-wide second. Safe from any thread.
/// </summary>
public sealed class EconomyLimits(TimeProvider time, int maxAddresses = EconomyLimits.MaxAddresses,
    int perSecond = EconomyLimits.PerSecond, int maxLookups = EconomyLimits.MaxLookups, int checkedCapacity = EconomyLimits.CheckedSessions)
{
    /// <summary>Calls one address may make over a rolling minute.</summary>
    public const int PerMinute = 60;

    /// <summary>Session lookups all addresses together may start over a rolling second.</summary>
    public const int PerSecond = 20;

    /// <summary>Addresses tracked. When the table is full after a due prune, a new address is refused.</summary>
    public const int MaxAddresses = 10_000;

    /// <summary>Session lookups at once.</summary>
    public const int MaxLookups = 8;

    /// <summary>Checked sessions remembered; the oldest is dropped first.</summary>
    public const int CheckedSessions = 10_000;

    /// <summary>How long a session that was looked up and found good is remembered.</summary>
    public static readonly TimeSpan CheckedFor = TimeSpan.FromMinutes(10);

    /// <summary>Lookups for one address that found no session; past this many in a minute the address gets no more lookups.</summary>
    public const int MissesPerMinute = 3;

    /// <summary>How long a pair that was looked up and found no session is answered as unknown without another lookup.</summary>
    public static readonly TimeSpan UnknownFor = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _calls = new();
    private readonly Queue<DateTimeOffset> _all = new();
    private readonly Dictionary<(Guid, Guid), DateTimeOffset> _checked = new();
    private readonly Queue<((Guid, Guid) Pair, DateTimeOffset At)> _checkedOrder = new();
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _misses = new();
    private readonly Dictionary<(Guid, Guid), DateTimeOffset> _unknown = new();
    private readonly Queue<((Guid, Guid) Pair, DateTimeOffset At)> _unknownOrder = new();
    private DateTimeOffset _pruned = DateTimeOffset.MinValue;
    private int _lookups;

    /// <summary>Counts this call and says whether it is within the limits.</summary>
    public bool Allow(IPAddress? ip)
    {
        // An unknown address has no per-address count, so it counts against the grid-wide second instead.
        if (ip is null) return TryTakeLookupSecond();
        // The table is keyed by the whole address. Kestrel listens on IPv4 only today; an IPv6 listener must key by the /64 (the
        // first 8 bytes), since one household or host holds a whole /64 of addresses.
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
            times.Enqueue(now);
            _calls[ip] = times;
            return true;
        }
    }

    /// <summary>Counts one session lookup against the grid-wide second; false when that second is used up.</summary>
    public bool TryTakeLookupSecond()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            while (_all.Count > 0 && now - _all.Peek() >= Second) _all.Dequeue();
            if (_all.Count >= perSecond) return false;
            _all.Enqueue(now);
            return true;
        }
    }

    /// <summary>True while this agent and secure session were found good within <see cref="CheckedFor"/>. Using it starts that time again.</summary>
    public bool IsChecked(Guid agent, Guid secure)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (!_checked.TryGetValue((agent, secure), out var at) || now - at >= CheckedFor) return false;
            Remember((agent, secure), now);
            return true;
        }
    }

    /// <summary>Remembers a session that was looked up and found good. Past the capacity, the oldest goes first.</summary>
    public void RememberChecked(Guid agent, Guid secure)
    {
        var now = time.GetUtcNow();
        lock (_gate) Remember((agent, secure), now);
    }

    private void Remember((Guid, Guid) key, DateTimeOffset now)
    {
        _checked[key] = now;
        _checkedOrder.Enqueue((key, now));
        // A pair remembered again leaves an older entry in the queue; only the entry matching its time counts.
        while (_checkedOrder.Count > 0 && (_checked.Count > checkedCapacity || now - _checkedOrder.Peek().At >= CheckedFor))
        {
            var (pair, at) = _checkedOrder.Dequeue();
            if (_checked.TryGetValue(pair, out var current) && current == at) _checked.Remove(pair);
        }
    }

    /// <summary>True while a lookup for this pair found no session within <see cref="UnknownFor"/>.</summary>
    public bool IsUnknown(Guid agent, Guid secure)
    {
        var now = time.GetUtcNow();
        lock (_gate) return _unknown.TryGetValue((agent, secure), out var at) && now - at < UnknownFor;
    }

    /// <summary>
    /// Records a lookup that found no session: the pair is remembered as unknown for a short time, and the address uses up one of its
    /// <see cref="MissesPerMinute"/>. Past the capacity, the oldest unknown pair goes first.
    /// </summary>
    public void RememberMiss(IPAddress? ip, Guid agent, Guid secure)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var key = (agent, secure);
            _unknown[key] = now;
            _unknownOrder.Enqueue((key, now));
            while (_unknownOrder.Count > 0 && (_unknown.Count > checkedCapacity || now - _unknownOrder.Peek().At >= UnknownFor))
            {
                var (pair, at) = _unknownOrder.Dequeue();
                if (_unknown.TryGetValue(pair, out var current) && current == at) _unknown.Remove(pair);
            }
            var address = Key(ip);
            if (!_misses.TryGetValue(address, out var times))
            {
                if (_misses.Count >= maxAddresses) PruneMisses(now);
                if (_misses.Count >= maxAddresses) return; // a full table counts no more; MayLookUp refuses the new addresses
                times = new Queue<DateTimeOffset>();
                _misses[address] = times;
            }
            times.Enqueue(now);
            while (times.Count > 0 && now - times.Peek() >= Window) times.Dequeue();
        }
    }

    /// <summary>False when this address already had <see cref="MissesPerMinute"/> lookups in the last minute that found no session.</summary>
    public bool MayLookUp(IPAddress? ip)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            var address = Key(ip);
            if (!_misses.TryGetValue(address, out var times)) return _misses.Count < maxAddresses;
            while (times.Count > 0 && now - times.Peek() >= Window) times.Dequeue();
            return times.Count < MissesPerMinute;
        }
    }

    private static IPAddress Key(IPAddress? ip) => ip is null ? IPAddress.None : ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    private void PruneMisses(DateTimeOffset now)
    {
        foreach (var (ip, times) in _misses.ToList())
            if (times.Count == 0 || now - times.Last() >= Window)
                _misses.Remove(ip);
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
