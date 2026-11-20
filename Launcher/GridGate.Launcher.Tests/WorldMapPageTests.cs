using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using Xunit;
using static GridGate.Launcher.Tests.WorldMapApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The world map page (spec 2026-11-20-public-map-design): an invitation with the live numbers for anyone not signed in, the
/// map for a signed-in resident or staff member, the Terms first for a resident who owes them, nothing without a guest side.
/// </summary>
public sealed class WorldMapPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url = "/map", string? language = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (language is not null) req.Headers.Add("Cookie", "gg-lang=" + language);
        using var r = await client.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static void TwoRegions(LauncherFactory f) => f.Regions.Result = new GridRegionList(
        [new GridRegion(Guid.NewGuid(), "Sandbox", 1002, 1000), new GridRegion(Guid.NewGuid(), "Welcome", 1000, 1000)], true);

    [Fact]
    public async Task Signed_out_sees_the_invitation()
    {
        await using var rig = SignUpsTests.Factory(open: true);
        TwoRegions(rig.F);

        using var r = await Client(rig.F).GetAsync("/map", Ct);
        var html = await r.Content.ReadAsStringAsync(Ct);
        var page = new HtmlParser().ParseDocument(html);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull(page.QuerySelector("#map-invite"));
        Assert.Equal("/guest/sign-in?returnUrl=%2Fmap", page.QuerySelector("a#map-sign-in")!.GetAttribute("href"));
        Assert.Equal("/signup", page.QuerySelector("a#map-join")!.GetAttribute("href"));
        Assert.Null(page.QuerySelector("#world-map"));
        Assert.DoesNotContain("Sandbox", html);
        Assert.DoesNotContain("Welcome", html);
        var numbers = page.QuerySelector("#map-numbers")!;
        Assert.Contains("Regions online", numbers.TextContent);
        Assert.Contains("2", numbers.QuerySelectorAll(".front-number-value").Select(v => v.TextContent.Trim()));
    }

    [Fact]
    public async Task No_join_while_sign_up_is_closed()
    {
        await using var rig = SignUpsTests.Factory(open: false);

        var page = await PageAsync(Client(rig.F));

        Assert.NotNull(page.QuerySelector("a#map-sign-in"));
        Assert.Null(page.QuerySelector("a#map-join"));
    }

    [Fact]
    public async Task A_resident_sees_the_map()
    {
        await using var f = Factory();

        var page = await PageAsync(await ResidentAsync(f));

        var map = page.QuerySelector("#world-map")!;
        Assert.Equal("/world/regions", map.GetAttribute("data-regions"));
        Assert.Equal("/world/tile", map.GetAttribute("data-tiles"));
        Assert.Equal("People here now: {0}", map.GetAttribute("data-people"));
        var scripts = page.QuerySelectorAll("script[src]").Select(s => s.GetAttribute("src")).ToList();
        Assert.True(scripts.IndexOf("/js/map-core.js") >= 0 && scripts.IndexOf("/js/map-core.js") < scripts.IndexOf("/js/world-map.js"));
        Assert.Null(page.QuerySelector("#map-invite"));
        Assert.NotNull(page.QuerySelector("#world-card a#world-teleport"));
        Assert.Equal("/guest/connect", page.QuerySelector("#world-card a.world-card-help")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Staff_see_the_map()
    {
        await using var f = Factory();

        var page = await PageAsync(await StaffAsync(f));

        Assert.NotNull(page.QuerySelector("#world-map"));
    }

    [Fact]
    public async Task Staff_outside_the_staff_networks_see_the_invitation()
    {
        await using var f = Factory(outside: true);
        var staff = await StaffAsync(f);
        f.Store.Save(f.Store.LoadOrCreate() with { Security = f.Store.LoadOrCreate().Security with { AdminNetworks = ["10.0.0.0/8"] } });

        var page = await PageAsync(staff);

        Assert.NotNull(page.QuerySelector("#map-invite"));
        Assert.Null(page.QuerySelector("#world-map"));
    }

    [Fact]
    public async Task A_resident_who_owes_the_Terms_goes_to_them_first()
    {
        await using var f = Factory(s => s with { Legal = Terms(1) });

        using var r = await (await ResidentAsync(f)).GetAsync("/map", Ct);

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/guest/terms?ReturnUrl=%2Fmap", r.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Not_there_without_a_guest_side()
    {
        await using var f = Factory(s => s with { Plan = OwnerPlan.RegionServer });

        using var r = await Client(f).GetAsync("/map", Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Menu_has_the_world_map()
    {
        await using var f = Factory();

        var page = await PageAsync(Client(f), "/home");

        Assert.Equal("/map", page.QuerySelector("a#menu-map")!.GetAttribute("href"));
    }

    [Fact]
    public async Task The_page_speaks_the_residents_language()
    {
        await using var f = Factory();

        var page = await PageAsync(Client(f), language: "de");

        Assert.Equal("Weltkarte", page.QuerySelector("h1")!.TextContent.Trim());
    }
}
