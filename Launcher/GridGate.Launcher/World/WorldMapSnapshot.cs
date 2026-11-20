using GridGate.Launcher.Core.Grid;

namespace GridGate.Launcher.World;

/// <summary>A region on the world map: its corner and size in map squares, and how many people are in it (null: unknown).</summary>
public sealed record WorldRegion(string Name, int X, int Y, int W, int H, int? People);

/// <param name="Available">False when the grid's regions couldn't be read; <see cref="Regions"/> is then empty.</param>
public sealed record WorldMapData(IReadOnlyList<WorldRegion> Regions, bool Available);

/// <summary>
/// The world map's regions (spec 2026-11-20-public-map-design, "The data links"): the grid's own (never hypergrid links), with
/// their sizes and people. Built at most once every <see cref="Lifetime"/> by the injected clock and shared by every caller, so
/// residents polling the map can't drive the database; concurrent callers share one build, as <c>FrontPageSnapshot</c> does.
/// </summary>
public sealed class WorldMapSnapshot(IOnlineUsers online, IGridRegions regions, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _build = new(1, 1);
    private volatile Cached? _cached;

    private sealed record Cached(WorldMapData Data, DateTimeOffset Until);

    public async Task<WorldMapData> CurrentAsync(CancellationToken ct)
    {
        if (Fresh() is { } data) return data;
        await _build.WaitAsync(ct);
        try
        {
            if (Fresh() is { } built) return built;
            var next = await BuildAsync(ct);
            _cached = new Cached(next, time.GetUtcNow() + Lifetime);
            return next;
        }
        finally
        {
            _build.Release();
        }
    }

    private WorldMapData? Fresh() => _cached is { } c && time.GetUtcNow() < c.Until ? c.Data : null;

    private async Task<WorldMapData> BuildAsync(CancellationToken ct)
    {
        var who = online.ListAsync(ct);
        var where = regions.ListAsync(ct);
        var (people, listed) = (await who, await where);
        if (!listed.Available) return new WorldMapData([], false);

        var peopleIn = people.Avatars.GroupBy(a => a.RegionId).ToDictionary(g => g.Key, g => g.Count());
        return new WorldMapData(
            [.. listed.Regions.Where(r => !r.IsLink).Select(r => new WorldRegion(r.Name, r.X, r.Y, r.SizeX, r.SizeY,
                people.Available ? peopleIn.GetValueOrDefault(r.Id) : null))],
            true);
    }
}
