using System;
using System.Collections.Generic;

namespace GridGate.Region.Money;

/// <summary>
/// How many script payouts one object owner may make a minute. Sixty is far above any vendor or tip-jar split; a script
/// looping on llGiveMoney would otherwise send the money service a payout every few milliseconds.
/// </summary>
internal sealed class PayoutLimit(TimeProvider time)
{
    public const int PerWindow = 60;
    public const int MaxOwners = 10_000;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private sealed class Owner
    {
        public readonly Queue<DateTimeOffset> Taken = new();
        public DateTimeOffset? Warned;
        public DateTimeOffset Last;
    }

    private readonly Dictionary<Guid, Owner> _owners = new();

    /// <summary>Owners remembered right now.</summary>
    public int Count
    {
        get { lock (_owners) return _owners.Count; }
    }

    /// <summary>
    /// Counts this payout; false when the owner already had <see cref="PerWindow"/> in the last <see cref="Window"/>.
    /// <paramref name="warn"/> is true for the first refusal in each minute, which is the one worth a log line.
    /// </summary>
    public bool TryTake(Guid owner, out bool warn)
    {
        var now = time.GetUtcNow();
        warn = false;
        lock (_owners)
        {
            if (!_owners.TryGetValue(owner, out var mine))
            {
                if (_owners.Count >= MaxOwners) Sweep(now);
                if (_owners.Count >= MaxOwners)
                {
                    warn = true;
                    return false;
                }
                _owners[owner] = mine = new Owner();
            }
            while (mine.Taken.Count > 0 && now - mine.Taken.Peek() >= Window) mine.Taken.Dequeue();
            if (mine.Taken.Count >= PerWindow)
            {
                if (mine.Warned is not { } at || now - at >= Window)
                {
                    mine.Warned = now;
                    warn = true;
                }
                return false;
            }
            mine.Taken.Enqueue(now);
            mine.Last = now;
            return true;
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        List<Guid>? old = null;
        foreach (var (id, o) in _owners)
            if (now - o.Last >= Window)
                (old ??= []).Add(id);
        if (old is null) return;
        foreach (var id in old) _owners.Remove(id);
    }
}
