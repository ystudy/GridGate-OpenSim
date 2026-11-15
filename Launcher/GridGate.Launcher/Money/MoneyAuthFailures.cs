using System.Collections.Concurrent;
using System.Net;

namespace GridGate.Launcher.Money;

/// <summary>
/// Wrong region keys per address (money design §7.2: "slowed down and logged"). Past <see cref="Limit"/> failures within
/// <see cref="Window"/>, each further wrong key waits <see cref="SlowDown"/> before its 401. Nothing is ever blocked: every
/// region program calls from 127.0.0.1, so blocking an address would let one stale key stop money for all of them (review
/// I2). One warning line per address per window, naming the address and the count, never what was sent.
/// </summary>
public sealed class MoneyAuthFailures(TimeProvider time, ILogger<MoneyAuthFailures> log)
{
    public const int Limit = 20;

    /// <summary>How many addresses are tracked before expired ones are dropped (review of plan 3, Minor 4).</summary>
    public const int MaxTallies = 4096;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SlowDown = TimeSpan.FromSeconds(1);

    private sealed class Tally
    {
        public DateTimeOffset Since;
        public int Count;
        public bool Warned;
        public bool DropWarned;
    }

    private readonly ConcurrentDictionary<IPAddress, Tally> _tallies = new();

    /// <summary>How many addresses are tracked now.</summary>
    public int Tracked => _tallies.Count;

    /// <summary>Counts a wrong key from <paramref name="ip"/>; how long to wait before answering it.</summary>
    public TimeSpan RecordFailure(IPAddress ip)
    {
        var t = TallyOf(ip);
        int count;
        bool warn;
        lock (t)
        {
            var now = time.GetUtcNow();
            if (now - t.Since > Window)
                (t.Since, t.Count, t.Warned) = (now, 0, false);
            count = ++t.Count;
            warn = count > Limit && !t.Warned;
            if (warn) t.Warned = true;
        }
        if (warn)
            log.LogWarning("Slowing down money requests with a wrong region key from {Address}: {Count} in the last {Minutes} minutes.",
                ip, count, (int)Window.TotalMinutes);
        return count > Limit ? SlowDown : TimeSpan.Zero;
    }

    /// <summary>
    /// Whether a connection dropped from <paramref name="ip"/> (it isn't on this network) is the first in this window, so
    /// the log gets one line per address per window instead of one per connection.
    /// </summary>
    public bool FirstDropInWindow(IPAddress ip)
    {
        var t = TallyOf(ip);
        lock (t)
        {
            var now = time.GetUtcNow();
            if (now - t.Since > Window)
                (t.Since, t.Count, t.Warned, t.DropWarned) = (now, 0, false, false);
            if (t.DropWarned) return false;
            t.DropWarned = true;
            return true;
        }
    }

    private Tally TallyOf(IPAddress ip)
    {
        var now = time.GetUtcNow();
        // Many fresh addresses (a forwarded port being scanned) must not grow this for ever: past the cap, expired ones go.
        if (_tallies.Count >= MaxTallies)
            foreach (var (address, old) in _tallies)
                if (now - old.Since > Window) _tallies.TryRemove(address, out _);
        return _tallies.GetOrAdd(ip, _ => new Tally { Since = now });
    }
}
