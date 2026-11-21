using GridGate.Launcher.Core.Grid;

namespace GridGate.Launcher.Featured;

/// <summary>A listed parcel and the online region it is on.</summary>
public sealed record FeaturedPlace(ListedParcel Parcel, GridRegion Region);

/// <param name="Available">False when the parcels or the regions couldn't be read; <see cref="Places"/> is then empty.</param>
public sealed record FeaturedData(IReadOnlyList<FeaturedPlace> Places, bool Available);

/// <summary>
/// The Featured page's directory (spec 2026-11-21-featured-places-design, "Which places show"): the parcels residents put in
/// search that are on one of this grid's own regions online now (never a hypergrid link, never a stopped region), so every
/// Teleport link can work. Built at most once every <see cref="Lifetime"/> and shared by every visitor, so public traffic
/// can't drive the database; concurrent callers share one build, as <c>WorldMapSnapshot</c> does.
/// </summary>
public sealed class FeaturedDirectory(IListedParcels listed, IGridRegions regions, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _build = new(1, 1);
    private volatile Cached? _cached;

    private sealed record Cached(FeaturedData Data, DateTimeOffset Until);

    public async Task<FeaturedData> CurrentAsync(CancellationToken ct)
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

    private FeaturedData? Fresh() => _cached is { } c && time.GetUtcNow() < c.Until ? c.Data : null;

    private async Task<FeaturedData> BuildAsync(CancellationToken ct)
    {
        var parcels = listed.ListAsync(ct);
        var online = regions.ListAsync(ct);
        var (list, where) = (await parcels, await online);
        if (!list.Available || !where.Available) return new FeaturedData([], false);

        var own = where.Regions.Where(r => !r.IsLink).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        return new FeaturedData(
            [.. list.Parcels.Where(p => own.ContainsKey(p.RegionId)).Select(p => new FeaturedPlace(p, own[p.RegionId]))],
            true);
    }
}
