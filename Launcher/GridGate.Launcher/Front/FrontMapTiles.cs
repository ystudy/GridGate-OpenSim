using System.Collections.Concurrent;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Endpoints;

namespace GridGate.Launcher.Front;

/// <summary>
/// The Automatic places' map tiles, kept per region for <see cref="FrontPageSnapshot.Lifetime"/>, so visitors don't each make
/// the launcher ask Robust (spec 2026-11-19-front-page-design, Constraints). Only <see cref="Endpoints.FrontApi"/> asks, and
/// only for a region on the Automatic list.
/// </summary>
public sealed class FrontMapTiles(IGridController grid, IHttpClientFactory http, TimeProvider time)
{
    private sealed record Kept(byte[] Bytes, string ContentType, DateTimeOffset Until);

    private readonly ConcurrentDictionary<Guid, Kept> _tiles = new();

    /// <summary>The region's tile; empty bytes when the grid has none yet (a missing tile is kept too, so it isn't asked again at once).</summary>
    public async Task<(byte[] Bytes, string ContentType)> TileAsync(FrontAutoPlace place, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_tiles.TryGetValue(place.RegionId, out var kept) && now < kept.Until) return (kept.Bytes, kept.ContentType);
        var (bytes, type) = await MapTiles.FetchAsync(new GridRegion(place.RegionId, place.Name, place.X, place.Y), grid, http, ct);
        _tiles[place.RegionId] = new Kept(bytes, type, now + FrontPageSnapshot.Lifetime);
        // Regions that left the list are dropped, so the cache never holds more than the list and a few recent ones.
        foreach (var (id, old) in _tiles)
            if (now >= old.Until) _tiles.TryRemove(id, out _);
        return (bytes, type);
    }
}
