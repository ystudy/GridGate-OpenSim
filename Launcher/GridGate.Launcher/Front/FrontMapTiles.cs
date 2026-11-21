using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Endpoints;

namespace GridGate.Launcher.Front;

/// <summary>
/// The Automatic places' map tiles, kept per region for <see cref="FrontPageSnapshot.Lifetime"/>, so visitors don't each make
/// the launcher ask Robust (spec 2026-11-19-front-page-design, Constraints). Visitors who come while a tile is missing or
/// expired share one ask. Only <see cref="Endpoints.FrontApi"/> asks, and only for a region on the Automatic list.
/// </summary>
public sealed class FrontMapTiles(IGridController grid, IHttpClientFactory http, TimeProvider time)
{
    private sealed record Kept(Task<(byte[] Bytes, string ContentType)> Tile, DateTimeOffset Until);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Kept> _tiles = [];

    /// <summary>The region's tile; empty bytes when the grid has none yet (a missing tile is kept too, so it isn't asked again at once).</summary>
    public Task<(byte[] Bytes, string ContentType)> TileAsync(FrontAutoPlace place, CancellationToken ct) =>
        TileAsync(new GridRegion(place.RegionId, place.Name, place.X, place.Y), ct);

    /// <summary>
    /// The tile of any region a public page shows (the Featured page's places too); the caller checks the page shows it.
    /// Empty bytes when the grid has none yet.
    /// </summary>
    public Task<(byte[] Bytes, string ContentType)> TileAsync(GridRegion region, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        Kept kept;
        lock (_gate)
        {
            if (!_tiles.TryGetValue(region.Id, out kept!) || now >= kept.Until || kept.Tile.IsFaulted || kept.Tile.IsCanceled)
            {
                // Not tied to this visitor's request: one who leaves mustn't cancel the ask the others are waiting on. The
                // map client's own timeout ends it.
                kept = new Kept(MapTiles.FetchAsync(region, grid, http, CancellationToken.None),
                    now + FrontPageSnapshot.Lifetime);
                _tiles[region.Id] = kept;
                // Regions that left the list are dropped, so the cache never holds more than the list and a few recent ones.
                foreach (var (id, old) in _tiles.ToList())
                    if (now >= old.Until) _tiles.Remove(id);
            }
        }
        return kept.Tile.WaitAsync(ct);
    }

    /// <summary>Whether the grid answered, within the kept time, that it has no tile for the region: the page shows the blank card.</summary>
    public bool KnownMissing(Guid regionId)
    {
        lock (_gate)
        {
            return _tiles.TryGetValue(regionId, out var kept) && time.GetUtcNow() < kept.Until
                && kept.Tile.IsCompletedSuccessfully && kept.Tile.Result.Bytes.Length == 0;
        }
    }
}
