using GridGate.Launcher.Core.Dashboard;
using GridGate.Launcher.Core.Grid;

namespace GridGate.Launcher.Front;

/// <summary>A region on the automatic places list: the grid's own, running, with how many people are in it now.</summary>
public sealed record FrontAutoPlace(Guid RegionId, string Name, int X, int Y, int People);

/// <summary>The front page's live data; a null number means it couldn't be counted, and the page hides its tile.</summary>
/// <param name="Busiest">At most <see cref="FrontPageSnapshot.MaxAutomatic"/> of the grid's own regions, most people first.</param>
public sealed record FrontLive(int? InWorld, int? RegionsOnline, long? Registered, long? ActiveThisMonth, IReadOnlyList<FrontAutoPlace> Busiest);

/// <summary>
/// What the front page shows live (spec 2026-11-19-front-page-design §2, §3): people in-world, the grid's own regions (never
/// hypergrid links), registered and recently active residents, and the busiest regions for the Automatic places. Built at
/// most once a minute by the injected clock and shared by every visitor, so public visits can't drive the database; concurrent
/// callers share one build, as <see cref="Splash.SplashSnapshot"/> does.
/// </summary>
public sealed class FrontPageSnapshot(IOnlineUsers online, IGridRegions regions, IResidentCounter residents, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    public const int MaxAutomatic = 6;
    /// <summary>"Active this month": signed in during the last 30 days, not the calendar month.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(30);

    private readonly SemaphoreSlim _build = new(1, 1);
    private volatile Cached? _cached;

    private sealed record Cached(FrontLive Live, DateTimeOffset Until);

    public async Task<FrontLive> CurrentAsync(CancellationToken ct)
    {
        if (Fresh() is { } live) return live;
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

    /// <summary>Drops the cached data, so a change on the staff page shows at once.</summary>
    public void Invalidate() => _cached = null;

    private FrontLive? Fresh() => _cached is { } c && time.GetUtcNow() < c.Until ? c.Live : null;

    private async Task<FrontLive> BuildAsync(CancellationToken ct)
    {
        var who = online.ListAsync(ct);
        var where = regions.ListAsync(ct);
        var counts = residents.CountAsync(time.GetUtcNow() - ActiveWindow, ct);
        var (people, listed, residentCounts) = (await who, await where, await counts);

        var own = listed.Regions.Where(r => !r.IsLink).ToList();
        var peopleIn = people.Avatars.GroupBy(a => a.RegionId).ToDictionary(g => g.Key, g => g.Count());
        var busiest = own
            .Select(r => new FrontAutoPlace(r.Id, r.Name, r.X, r.Y, peopleIn.GetValueOrDefault(r.Id)))
            .OrderByDescending(p => p.People).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAutomatic).ToList();

        return new FrontLive(
            people.Available ? people.Avatars.Count : null,
            listed.Available ? own.Count : null,
            residentCounts.Registered,
            residentCounts.ActiveSince,
            busiest);
    }
}
