using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Residents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The Avatar page (spec 2026-11-21-avatar-page-design): the Avatar tab shows the signed-in resident's own avatar and what it
/// wears; the tab bar joins its five pages for a resident; the picture route answers only with the asker's own picture.
/// </summary>
public sealed class AvatarPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid Me = TestAuth.GuestId;
    private static readonly Guid Photo = Guid.Parse("e0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid WelcomeId = Guid.Parse("70000000-0000-0000-0000-000000000001");

    private static PublicProfile Profile(Guid image) =>
        new(Me, "Test", "Guest", new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero), image, "", "", "", "", "", []);

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)r.StatusCode}");
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static string Text(IHtmlDocument page, string selector) => page.QuerySelector(selector)?.TextContent.Trim() ?? "";

    [Fact]
    public async Task Avatar_tab_shows_their_avatar_and_what_it_wears()
    {
        await using var f = WorldMapApiTests.Factory();
        f.Profiles.Add(Profile(Photo));
        f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Test Guest", WelcomeId, Me)], true);
        var folder = f.Inventory.AddDefaultFolders(Me);
        var shape = f.Inventory.Add(Me, folder, "Tall shape", assetType: 13);
        var shirt = f.Inventory.Add(Me, folder, "Blue shirt", assetType: 5);
        var hat = f.Inventory.Add(Me, folder, "Captain hat", assetType: 6);
        var hud = f.Inventory.Add(Me, folder, "Dance HUD", assetType: 6);
        f.Appearance.Set(Me, [new WornItem("Wearable 0:0", shape.ItemId), new WornItem("Wearable 4:0", shirt.ItemId),
            new WornItem("_ap_2", hat.ItemId), new WornItem("_ap_35", hud.ItemId), new WornItem("_ap_6", Guid.NewGuid())]);

        var page = await PageAsync(await WorldMapApiTests.ResidentAsync(f), "/guest");

        Assert.Equal("Test Guest", Text(page, "main h1#avatar-name"));
        Assert.Equal("Member since March 2026", Text(page, ".avatar-since"));
        Assert.Equal("/guest/avatar.jpg", page.QuerySelector("img#avatar-photo")!.GetAttribute("src"));
        Assert.Equal("In-world now in Welcome", Text(page, "#avatar-presence"));
        Assert.Equal("/guest/account#public-profile", page.QuerySelector("a#avatar-public-page")!.GetAttribute("href"));

        Assert.Equal("Shape Tall shape", string.Join(' ', page.QuerySelectorAll("#avatar-body li span").Select(s => s.TextContent.Trim()).Take(2)));
        Assert.Contains("Blue shirt", Text(page, "#avatar-clothes"));
        Assert.Equal(["Captain hat", "An item no longer in your inventory"],
            page.QuerySelectorAll("#avatar-attached .avatar-item").Select(s => s.TextContent.Trim()));
        Assert.Contains("Dance HUD", Text(page, "#avatar-huds"));

        // Get in-world and their pages are still here.
        Assert.Contains("Connect with Firestorm", Text(page, "#avatar-get-in"));
        Assert.NotNull(page.QuerySelector("form[action='/guest/sign-out']"));
    }

    [Fact]
    public async Task On_the_residents_page_it_links_their_public_page()
    {
        await using var f = WorldMapApiTests.Factory();
        f.Profiles.Add(Profile(Guid.Empty));
        f.Services.GetRequiredService<PublicProfileChoices>().Choose(Me, true);

        var page = await PageAsync(await WorldMapApiTests.ResidentAsync(f), "/guest");

        Assert.Equal("/residents/Test/Guest", page.QuerySelector("a#avatar-public-page")!.GetAttribute("href"));
        Assert.NotNull(page.QuerySelector("#avatar-photo-none"));
        Assert.Equal("Not in-world now", Text(page, "#avatar-presence"));
    }

    [Fact]
    public async Task No_look_saved_yet_and_look_unavailable()
    {
        await using var f = WorldMapApiTests.Factory();
        var client = await WorldMapApiTests.ResidentAsync(f);

        Assert.NotNull((await PageAsync(client, "/guest")).QuerySelector("#avatar-look-none"));
        f.Appearance.Down = true;
        Assert.NotNull((await PageAsync(client, "/guest")).QuerySelector("#avatar-look-unavailable"));
    }

    [Theory]
    [InlineData("/guest", "avatar")]
    [InlineData("/map", "map")]
    [InlineData("/guest/account", "profile")]
    [InlineData("/guest/connect", "download")]
    public async Task Tab_bar_joins_the_residents_pages(string url, string current)
    {
        await using var f = WorldMapApiTests.Factory();
        var page = await PageAsync(await WorldMapApiTests.ResidentAsync(f), url);

        var tabs = page.QuerySelectorAll("#avatar-tabs a").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal(["/guest", "/map", "/guest/account", "/guest/connect"], tabs); // no inventory backups here
        var active = page.QuerySelector("#avatar-tabs a[aria-current=page]")!;
        Assert.Equal("avatar-tab-" + current, active.Id);
    }

    [Theory]
    [InlineData("/map")]
    [InlineData("/guest/connect")]
    [InlineData("/featured")]
    public async Task No_tab_bar_for_a_visitor_or_another_page(string url)
    {
        await using var f = WorldMapApiTests.Factory();
        Assert.Null((await PageAsync(WorldMapApiTests.Client(f), url)).QuerySelector("#avatar-tabs"));
        if (url == "/featured")
            Assert.Null((await PageAsync(await WorldMapApiTests.ResidentAsync(f), url)).QuerySelector("#avatar-tabs"));
    }

    [Fact]
    public async Task Inventory_tab_where_that_page_is_open_to_them()
    {
        await using var f = WorldMapApiTests.Factory(s => s with { Iar = s.Iar with { DownloadsOn = true } });
        var page = await PageAsync(await WorldMapApiTests.ResidentAsync(f), "/guest/inventory");
        Assert.Equal("avatar-tab-inventory", page.QuerySelector("#avatar-tabs a[aria-current=page]")!.Id);
    }

    [Fact]
    public async Task Menu_links_the_avatar_page()
    {
        await using var f = WorldMapApiTests.Factory();
        var page = await PageAsync(WorldMapApiTests.Client(f), "/home");
        Assert.Equal("/guest", page.QuerySelector("#menu-avatar")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Picture_is_their_own_and_only_for_them()
    {
        await using var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddHttpClient(Featured.ParcelPictures.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new FeaturedPageTests.SnapshotRobust(Photo)),
        };
        TestAuth.AddStaff(f.Robust);
        var visitor = WorldMapApiTests.Client(f);
        using (var anonymous = await visitor.GetAsync("/guest/avatar.jpg", Ct))
            Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);

        var me = await WorldMapApiTests.ResidentAsync(f);
        using (var none = await me.GetAsync("/guest/avatar.jpg", Ct))
            Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        f.Profiles.Add(Profile(Photo));
        using var mine = await me.GetAsync("/guest/avatar.jpg", Ct);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(FeaturedPageTests.SnapshotRobust.Jpeg, await mine.Content.ReadAsByteArrayAsync(Ct));
        Assert.True(mine.Headers.CacheControl is { Private: true, Public: false, MaxAge.TotalSeconds: 300 }, mine.Headers.CacheControl?.ToString());
    }
}
