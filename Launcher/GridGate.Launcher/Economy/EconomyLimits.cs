using System.Net;

namespace GridGate.Launcher.Economy;

/// <summary>
/// How often one address may call the economy helper: <see cref="PerMinute"/> calls over a rolling minute (money design
/// §8.5). In memory only; an IPv4-mapped IPv6 address counts as its IPv4 address, and an unknown address counts nothing.
/// Safe from any thread.
/// </summary>
public sealed class EconomyLimits(TimeProvider time)
{
    public const int PerMinute = 60;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxAddresses = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _calls = new();
    private DateTimeOffset _pruned = DateTimeOffset.MinValue;

    /// <summary>Counts this call and says whether it is within the limit.</summary>
    public bool Allow(IPAddress? ip)
    {
        if (ip is null) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var now = time.GetUtcNow();
        lock (_gate)
        {
            // Old addresses are dropped at most once a minute, so many addresses can't make every call walk the table.
            if (_calls.Count >= MaxAddresses && now - _pruned >= Window) Prune(now);
            if (!_calls.TryGetValue(ip, out var times)) _calls[ip] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && now - times.Peek() >= Window) times.Dequeue();
            if (times.Count >= PerMinute) return false;
            times.Enqueue(now);
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        _pruned = now;
        foreach (var (ip, times) in _calls.ToList())
            if (times.Count == 0 || now - times.Last() >= Window)
                _calls.Remove(ip);
    }
}
