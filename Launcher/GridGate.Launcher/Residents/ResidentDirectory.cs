using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Guests;

namespace GridGate.Launcher.Residents;

/// <param name="Profiles">The residents shown, as read.</param>
/// <param name="Regions">This grid's own regions online now, by name (case ignored), for the picks' Teleport links.</param>
/// <param name="Available">False when the profiles couldn't be read; <see cref="Profiles"/> is then empty.</param>
public sealed record ResidentData(IReadOnlyList<PublicProfile> Profiles, IReadOnlyDictionary<string, GridRegion> Regions, bool Available);

/// <summary>
/// The Residents page's directory (spec 2026-11-21-resident-profiles-design, "Who shows"): the residents who turned it on from
/// their Account page (<see cref="PublicProfileChoices"/>), as <see cref="IPublicProfiles"/> reads them (people on this grid,
/// not blocked). Built at most once every <see cref="Lifetime"/> and shared by every visitor, as <c>FeaturedDirectory</c> is;
/// <see cref="Invalidate"/> drops it so a resident's own change shows at once.
/// </summary>
public sealed class ResidentDirectory(IPublicProfiles profiles, PublicProfileChoices choices, IGridRegions regions, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _build = new(1, 1);
    private volatile Cached? _cached;

    private sealed record Cached(ResidentData Data, DateTimeOffset Until);

    public async Task<ResidentData> CurrentAsync(CancellationToken ct)
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

    /// <summary>Drops the shared directory: the next visitor builds it afresh.</summary>
    public void Invalidate() => _cached = null;

    private ResidentData? Fresh() => _cached is { } c && time.GetUtcNow() < c.Until ? c.Data : null;

    private async Task<ResidentData> BuildAsync(CancellationToken ct)
    {
        var shown = choices.Shown();
        var read = profiles.ReadAsync(shown, ct);
        var online = regions.ListAsync(ct);
        var (list, where) = (await read, await online);
        if (!list.Available) return new ResidentData([], new Dictionary<string, GridRegion>(), false);

        // A choice made just now could be undone before the read finished: only those still shown.
        var still = choices.Shown().ToHashSet();
        var own = where.Available
            ? where.Regions.Where(r => !r.IsLink).GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, GridRegion>(StringComparer.OrdinalIgnoreCase);
        return new ResidentData([.. list.Profiles.Where(p => still.Contains(p.Id))], own, true);
    }
}
