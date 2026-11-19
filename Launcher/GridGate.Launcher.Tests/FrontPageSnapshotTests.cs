using GridGate.Launcher.Core.Dashboard;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Front;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The front page's live numbers and busiest regions, built at most once a minute (spec 2026-11-19-front-page-design §2, §3).</summary>
public sealed class FrontPageSnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeOnlineUsers _online = new();
    private readonly FakeGridRegions _regions = new();
    private readonly FakeResidentCounter _residents = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero));

    private FrontPageSnapshot Snapshot() => new(_online, _regions, _residents, _time);

    private static GridRegion Region(string name, bool link = false) => new(Guid.NewGuid(), name, 1000, 1000, link);

    private static OnlineAvatar In(GridRegion r) => new("Someone Here", r.Id, Guid.NewGuid());

    [Fact]
    public async Task Counts_leave_out_hypergrid_links()
    {
        var (a, b, link) = (Region("Alpha"), Region("Beta"), Region("Elsewhere", link: true));
        _regions.Result = new GridRegionList([a, b, link], true);
        _online.Result = new OnlineList([In(a), In(link)], true);
        _residents.Counts = new ResidentCounts(120, 30);

        var live = await Snapshot().CurrentAsync(Ct);

        Assert.Equal(2, live.InWorld);
        Assert.Equal(2, live.RegionsOnline);
        Assert.Equal(120, live.Registered);
        Assert.Equal(30, live.ActiveThisMonth);
        Assert.Equal(_time.GetUtcNow().AddDays(-30), _residents.LastSince);
    }

    [Fact]
    public async Task Busiest_orders_by_people_then_name_and_takes_six()
    {
        var regions = new[] { "Hotel", "alpha", "Golf", "Bravo", "Foxtrot", "Echo", "Delta", "Charlie" }.Select(n => Region(n)).ToList();
        var link = Region("Crowded Link", link: true);
        _regions.Result = new GridRegionList([.. regions, link], true);
        var byName = regions.ToDictionary(r => r.Name);
        _online.Result = new OnlineList(
        [
            In(byName["Golf"]), In(byName["Golf"]), In(byName["Golf"]),
            In(byName["Hotel"]), In(byName["Hotel"]),
            In(link), In(link), In(link), In(link),
        ], true);

        var live = await Snapshot().CurrentAsync(Ct);

        // Golf (3), Hotel (2), then the empty ones by name, ignoring case; never the link.
        Assert.Equal(["Golf", "Hotel", "alpha", "Bravo", "Charlie", "Delta"], live.Busiest.Select(p => p.Name));
        Assert.Equal([3, 2, 0, 0, 0, 0], live.Busiest.Select(p => p.People));
        Assert.Equal(byName["Golf"].Id, live.Busiest[0].RegionId);
    }

    [Fact]
    public async Task Unavailable_sources_give_nulls_and_no_places()
    {
        _regions.Result = GridRegionList.Unavailable;
        _online.Result = OnlineList.Unavailable;
        _residents.Counts = ResidentCounts.Unknown;

        var live = await Snapshot().CurrentAsync(Ct);

        Assert.Equal(new FrontLive(null, null, null, null, []), live with { Busiest = [] });
        Assert.Empty(live.Busiest);
    }

    [Fact]
    public async Task Builds_at_most_once_a_minute()
    {
        var snapshot = Snapshot();

        await snapshot.CurrentAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(30));
        await snapshot.CurrentAsync(Ct);
        Assert.Equal(1, _residents.Calls);

        _time.Advance(TimeSpan.FromSeconds(31));
        await snapshot.CurrentAsync(Ct);
        Assert.Equal(2, _residents.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_build()
    {
        var snapshot = Snapshot();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => snapshot.CurrentAsync(Ct)));

        Assert.Equal(1, _residents.Calls);
    }

    [Fact]
    public async Task Invalidate_rebuilds_on_the_next_call()
    {
        var snapshot = Snapshot();
        await snapshot.CurrentAsync(Ct);

        snapshot.Invalidate();
        await snapshot.CurrentAsync(Ct);

        Assert.Equal(2, _residents.Calls);
    }
}
