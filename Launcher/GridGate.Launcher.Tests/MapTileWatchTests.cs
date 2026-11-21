using System.Text.Json.Nodes;
using Jint;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Which map tiles are fetched again after a poll (wwwroot/js/map-core.js, <c>mapTileWatch</c>), run through Jint. Seen on
/// the Debian 12 VM: R3 moved from 1003,1003 to 1002,1002 and the map kept its old picture at the old place and water at
/// the new one. A move's restart fitted between two polls, so the only refresh came before R3 sent its new picture, and
/// only the new place was refreshed. Then, zoomed out, the old picture stayed: Robust rebuilds its zoomed-out tiles a
/// minute after a change (MapImageService waits 60 seconds), long after a watch of a few polls had ended.
/// </summary>
public sealed class MapTileWatchTests
{
    private const int SettleMs = 70_000;

    private static readonly string Script = File.ReadAllText(Path.Combine(Root(), "Launcher", "GridGate.Launcher", "wwwroot", "js", "map-core.js"));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private readonly Engine _js = new Engine()
        .Execute(Script)
        .Execute($"var watch = mapTileWatch(function (r) {{ return r.id; }}, {SettleMs});");

    private static string R(string id, int x, int y, string state = "running", int size = 1) =>
        $"{{ id: '{id}', name: '{id}', x: {x}, y: {y}, size: {size}, state: '{state}' }}";

    /// <summary>The places to fetch again after a poll at <paramref name="at"/> ms that went from <paramref name="before"/> to <paramref name="after"/>, as "x,y wxh".</summary>
    private List<string> Next(int at, string before, string after)
    {
        var json = JsonNode.Parse(_js.Evaluate($"JSON.stringify(watch.next([{before}], [{after}], {at}))").AsString())!.AsArray();
        return json.Select(p => $"{(int)p!["x"]!},{(int)p["y"]!} {(int)p["w"]!}x{(int)p["h"]!}").Order().ToList();
    }

    [Fact]
    public void A_moved_region_has_both_places_fetched_until_Robust_has_rebuilt_its_zoomed_out_tiles()
    {
        string[] both = ["1002,1002 1x1", "1003,1003 1x1"];
        string moved = R("r3", 1002, 1002);

        Assert.Equal(both, Next(0, R("r3", 1003, 1003), moved));
        // The restart may fit between polls: the new picture arrives after the poll that saw the move.
        Assert.Equal(both, Next(5_000, moved, moved));
        // Robust rebuilds the zoomed-out tiles a minute after the change.
        Assert.Equal(both, Next(65_000, moved, moved));
        Assert.Equal(both, Next(70_000, moved, moved));
        Assert.Empty(Next(75_000, moved, moved));
    }

    [Fact]
    public void Regions_that_stay_the_same_fetch_nothing()
    {
        Assert.Empty(Next(0, R("home", 1000, 1000), R("home", 1000, 1000)));
    }

    [Fact]
    public void A_change_while_settling_starts_the_clock_again()
    {
        string starting = R("r3", 1002, 1002, state: "starting"), running = R("r3", 1002, 1002);
        Next(0, R("r3", 1003, 1003), starting);
        Next(5_000, starting, starting);
        // Running from 10 s: watched until 80 s, still both places.
        Assert.Equal(["1002,1002 1x1", "1003,1003 1x1"], Next(10_000, starting, running));
        Assert.Equal(2, Next(75_000, running, running).Count);
        Assert.Equal(2, Next(80_000, running, running).Count);
        Assert.Empty(Next(85_000, running, running));
    }

    [Fact]
    public void Added_and_removed_regions_have_their_places_fetched()
    {
        Assert.Equal(["1005,1005 2x2"], Next(0, "", R("big", 1005, 1005, size: 2)));
        Assert.Equal(["1000,1000 1x1", "1005,1005 2x2"], Next(5_000, R("big", 1005, 1005, size: 2) + "," + R("home", 1000, 1000), R("big", 1005, 1005, size: 2)));
        // Each is watched for its own time: the added one until 70 s, the removed one until 75 s.
        string big = R("big", 1005, 1005, size: 2);
        Assert.Equal(2, Next(70_000, big, big).Count);
        Assert.Equal(["1000,1000 1x1"], Next(72_000, big, big));
        Assert.Equal(["1000,1000 1x1"], Next(75_000, big, big));
        Assert.Empty(Next(80_000, big, big));
    }
}
