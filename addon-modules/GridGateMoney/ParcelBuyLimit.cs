using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace GridGate.Region.Money;

/// <summary>
/// How often one avatar may send a ParcelBuy. Each one holds the region's packet thread while the money service is asked,
/// so an avatar sending them over and over could keep the region from handling anyone else's packets.
/// </summary>
internal sealed class ParcelBuyLimit(TimeProvider time)
{
    public const int PerWindow = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _taken = new();

    /// <summary>Counts this ParcelBuy; false when the agent already had <see cref="PerWindow"/> in the last <see cref="Window"/>.</summary>
    public bool TryTake(Guid agent)
    {
        var now = time.GetUtcNow();
        var mine = _taken.GetOrAdd(agent, _ => new Queue<DateTimeOffset>());
        lock (mine)
        {
            while (mine.Count > 0 && now - mine.Peek() >= Window) mine.Dequeue();
            if (mine.Count >= PerWindow) return false;
            mine.Enqueue(now);
            return true;
        }
    }

    /// <summary>How many avatars are remembered.</summary>
    public int Count => _taken.Count;

    /// <summary>
    /// The agent's client closed. Its count stays until it expires (closing the client and coming back, as a teleport out
    /// and back does, doesn't start it over); an entry with nothing left in the window is dropped.
    /// </summary>
    public void Forget(Guid agent)
    {
        if (!_taken.TryGetValue(agent, out var mine)) return;
        var now = time.GetUtcNow();
        lock (mine)
        {
            while (mine.Count > 0 && now - mine.Peek() >= Window) mine.Dequeue();
            if (mine.Count == 0) _taken.TryRemove(KeyValuePair.Create(agent, mine));
        }
    }
}
