using System.Text.Json.Nodes;
using Jint;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Which map tiles are fetched again after a poll (wwwroot/js/map-core.js, <c>mapTileWatch</c>), run through Jint. Seen on
/// the Debian 12 VM: R3 moved from 1003,1003 to 1002,1002 and the map kept its old picture at the old place and water at
/// the new one. A move's restart fitted between two polls, so the only refresh came before R3 sent its new picture, and
/// only the new place was refreshed.
/// </summary>
public sealed class MapTileWatchTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(Root(), "Launcher", "GridGate.Launcher", "wwwroot", "js", "map-core.js"));

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private readonly Engine _js = new Engine()
        .Execute(Script)
        .Execute("var watch = mapTileWatch(function (r) { return r.id; }, 2);");

    private static string R(string id, int x, int y, string state = "running", int size = 1) =>
        $"{{ id: '{id}', name: '{id}', x: {x}, y: {y}, size: {size}, state: '{state}' }}";

    /// <summary>The places to fetch again after a poll that went from <paramref name="before"/> to <paramref name="after"/>, as "x,y wxh".</summary>
    private List<string> Next(string before, string after)
    {
        var json = JsonNode.Parse(_js.Evaluate($"JSON.stringify(watch.next([{before}], [{after}]))").AsString())!.AsArray();
        return json.Select(p => $"{(int)p!["x"]!},{(int)p["y"]!} {(int)p["w"]!}x{(int)p["h"]!}").Order().ToList();
    }

    [Fact]
    public void A_moved_region_has_both_places_fetched_until_it_stays_put_for_two_polls()
    {
        string[] both = ["1002,1002 1x1", "1003,1003 1x1"];

        Assert.Equal(both, Next(R("r3", 1003, 1003), R("r3", 1002, 1002)));
        // The restart may fit between polls: the new picture arrives after the poll that saw the move.
        Assert.Equal(both, Next(R("r3", 1002, 1002), R("r3", 1002, 1002)));
        Assert.Equal(both, Next(R("r3", 1002, 1002), R("r3", 1002, 1002)));
        Assert.Empty(Next(R("r3", 1002, 1002), R("r3", 1002, 1002)));
    }

    [Fact]
    public void Regions_that_stay_the_same_fetch_nothing()
    {
        Assert.Empty(Next(R("home", 1000, 1000), R("home", 1000, 1000)));
    }

    [Fact]
    public void A_change_while_settling_starts_the_count_again()
    {
        Next(R("r3", 1003, 1003), R("r3", 1002, 1002, state: "starting"));
        Next(R("r3", 1002, 1002, state: "starting"), R("r3", 1002, 1002, state: "starting"));
        // Running now: two more polls, still both places.
        Assert.Equal(["1002,1002 1x1", "1003,1003 1x1"], Next(R("r3", 1002, 1002, state: "starting"), R("r3", 1002, 1002)));
        Assert.Equal(2, Next(R("r3", 1002, 1002), R("r3", 1002, 1002)).Count);
        Assert.Equal(2, Next(R("r3", 1002, 1002), R("r3", 1002, 1002)).Count);
        Assert.Empty(Next(R("r3", 1002, 1002), R("r3", 1002, 1002)));
    }

    [Fact]
    public void Added_and_removed_regions_have_their_places_fetched()
    {
        Assert.Equal(["1005,1005 2x2"], Next("", R("big", 1005, 1005, size: 2)));
        Assert.Equal(["1000,1000 1x1", "1005,1005 2x2"], Next(R("big", 1005, 1005, size: 2) + "," + R("home", 1000, 1000), R("big", 1005, 1005, size: 2)));
    }
}
