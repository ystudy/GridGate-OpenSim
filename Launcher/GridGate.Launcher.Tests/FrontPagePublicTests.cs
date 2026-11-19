using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Dashboard;
using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Splash;
using GridGate.Launcher.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The public front page, section by section (spec 2026-11-19-front-page-design, "The page, top to bottom").</summary>
public sealed class FrontPagePublicTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SplashFont Font = new(24, "#ffffff", false, SplashAlign.Left, SplashFontFamily.Sans);

    /// <summary>A grid named Avalon with its legal pages written; sign-up open unless asked otherwise.</summary>
    private static SignUpsTests.Rig Rig(bool open = true) => SignUpsTests.Factory(open: open);

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    private static async Task<IDocument> PageAsync(HttpClient client, string? language = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/home");
        if (language is not null) req.Headers.Add("Cookie", "gg-lang=" + language);
        using var r = await client.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static void Save(LauncherFactory f, FrontPageSettings s) => Assert.Empty(f.Services.GetRequiredService<FrontPageStore>().Save(s));

    private static Dictionary<string, string> En(string text) => new() { ["en"] = text };

    private static SplashBlock Block(int id, SplashBlockKind kind, SplashContent content) =>
        new(id, kind, 0, id * 100, 400, 90, id, "#000000", 0, null, false, 0, Font, content);

    private static void PublishSplash(LauncherFactory f, params SplashBlock[] blocks)
    {
        var store = f.Services.GetRequiredService<SplashStore>();
        var saved = store.SaveDraft(new SplashDesign(1280, 800, new SplashBackground("#000000", null, 50, 50, 0), false, 16, blocks), 0, "Test Owner");
        Assert.IsType<SplashSaveResult.Saved>(saved);
        Assert.NotNull(store.Publish("Test Owner"));
    }

    // ---- The top section ----

    [Fact]
    public async Task Hero_has_name_intro_and_create_account_while_sign_up_is_open()
    {
        await using var rig = Rig();

        var page = await PageAsync(Client(rig.F));

        var hero = page.QuerySelector("main section#front-hero")!;
        Assert.Equal("Avalon", hero.QuerySelector("h1")!.TextContent.Trim());
        Assert.Equal("Make an avatar and explore our world with us.", hero.QuerySelector("p.tone-text")!.TextContent.Trim());
        Assert.Equal("/signup", hero.QuerySelector("a#front-main")!.GetAttribute("href"));
        Assert.Equal("Create an account", hero.QuerySelector("a#front-main")!.TextContent.Trim());
        Assert.Equal("/guest/connect", hero.QuerySelector("a#front-connect")!.GetAttribute("href"));
        Assert.Equal("/signup", page.QuerySelector("main section#front-join a#front-join-main")!.GetAttribute("href"));
        Assert.Null(page.QuerySelector("form#front-sign-in")); // the old sign-in card is gone
    }

    [Fact]
    public async Task Main_button_is_sign_in_while_sign_up_is_closed()
    {
        await using var rig = Rig(open: false);

        var page = await PageAsync(Client(rig.F));

        Assert.Equal("/guest/sign-in", page.QuerySelector("a#front-main")!.GetAttribute("href"));
        Assert.Equal("Sign in", page.QuerySelector("a#front-main")!.TextContent.Trim());
        Assert.Equal("/guest/sign-in", page.QuerySelector("a#front-join-main")!.GetAttribute("href"));
    }

    [Fact]
    public async Task Main_button_is_your_account_for_a_resident()
    {
        await using var rig = Rig();
        var client = Client(rig.F);
        (await client.GetAsync("/__test/guest-signin", Ct)).Dispose();

        var page = await PageAsync(client);

        Assert.Equal("/guest", page.QuerySelector("a#front-main")!.GetAttribute("href"));
        Assert.Equal("Your account", page.QuerySelector("a#front-main")!.TextContent.Trim());
        Assert.Null(page.QuerySelector("#front-join")); // nowhere further to send them
    }

    [Fact]
    public async Task Hero_uses_the_photo_or_the_gradient()
    {
        await using var rig = Rig();
        var page = await PageAsync(Client(rig.F));
        Assert.Null(page.QuerySelector("#front-hero img"));
        Assert.Contains("front-hero-plain", page.QuerySelector("#front-hero")!.ClassList);

        var photo = Assert.IsType<PictureResult.Added>(await rig.F.Services.GetRequiredKeyedService<PictureStore>("front")
            .AddAsync(new MemoryStream(TestPictures.Png(64, 64)), "photo.png", "Test Owner", Ct)).Picture.Name;
        Save(rig.F, FrontPageSettings.Default with { Photo = photo });
        page = await PageAsync(Client(rig.F));

        var img = page.QuerySelector("#front-hero img.front-hero-photo")!;
        Assert.Equal(FrontApi.PictureUrl(photo), img.GetAttribute("src"));
        Assert.Equal("", img.GetAttribute("alt")); // decoration: the grid's name is the heading
    }

    // ---- Live numbers ----

    [Fact]
    public async Task Numbers_show_only_switched_on_known_values()
    {
        await using var rig = Rig();
        var a = new GridRegion(Guid.NewGuid(), "Alpha", 1000, 1000);
        rig.F.Regions.Result = new GridRegionList([a, new(Guid.NewGuid(), "Link", 2000, 2000, IsLink: true)], true);
        rig.F.OnlineUsers.Result = OnlineList.Unavailable; // In-world: unknown, so hidden
        rig.F.Residents.Counts = new ResidentCounts(1204, 87);
        Save(rig.F, FrontPageSettings.Default with { Numbers = new FrontNumbers(Active: false) });

        var page = await PageAsync(Client(rig.F));

        var tiles = page.QuerySelectorAll("#front-numbers .front-number")
            .Select(t => (t.QuerySelector(".front-number-value")!.TextContent.Trim(), t.QuerySelector(".front-number-label")!.TextContent.Trim()));
        Assert.Equal([("1", "Regions online"), ("1,204", "Registered avatars")], tiles);
    }

    [Fact]
    public async Task Numbers_section_is_hidden_when_the_database_is_down()
    {
        await using var rig = Rig();
        rig.F.Regions.Result = GridRegionList.Unavailable;
        rig.F.OnlineUsers.Result = OnlineList.Unavailable;
        rig.F.Residents.Counts = ResidentCounts.Unknown;
        Save(rig.F, FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Automatic });

        var page = await PageAsync(Client(rig.F));

        Assert.Null(page.QuerySelector("#front-numbers"));
        Assert.Null(page.QuerySelector("#front-places"));
        Assert.NotNull(page.QuerySelector("#front-hero"));
        Assert.NotNull(page.QuerySelector("#front-join"));
    }

    // ---- Featured places ----

    [Fact]
    public async Task Places_come_from_the_published_splash_by_default()
    {
        await using var rig = Rig();
        PublishSplash(rig.F,
            Block(1, SplashBlockKind.Places, new(Places: [new SplashPlace(Guid.NewGuid(), "Harbour", En("Boats"), null)])),
            Block(2, SplashBlockKind.Places, new(Places: [new SplashPlace(Guid.NewGuid(), "Hills", En("Views"), null)])));

        var page = await PageAsync(Client(rig.F));

        Assert.Equal(["Harbour", "Hills"], page.QuerySelectorAll("#front-places .front-card .front-card-name").Select(e => e.TextContent.Trim()));
        Assert.Equal(["Boats", "Views"], page.QuerySelectorAll("#front-places .front-card .front-card-line").Select(e => e.TextContent.Trim()));
    }

    [Fact]
    public async Task Places_section_is_hidden_with_no_published_splash()
    {
        await using var rig = Rig();

        var page = await PageAsync(Client(rig.F));

        Assert.Null(page.QuerySelector("#front-places"));
        Assert.Null(page.QuerySelector("#front-news"));
    }

    [Fact]
    public async Task Places_from_the_own_list_and_automatic()
    {
        await using var rig = Rig();
        var pic = Assert.IsType<PictureResult.Added>(await rig.F.Services.GetRequiredKeyedService<PictureStore>("front")
            .AddAsync(new MemoryStream(TestPictures.Png(64, 64)), "p.png", "Test Owner", Ct)).Picture.Name;
        var own = FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Own, Places = [new SplashPlace(Guid.NewGuid(), "Harbour", En("Boats"), pic)] };
        Save(rig.F, own);

        var page = await PageAsync(Client(rig.F));
        Assert.Equal(FrontApi.PictureUrl(pic), page.QuerySelector("#front-places .front-card img")!.GetAttribute("src"));

        var busy = new GridRegion(Guid.NewGuid(), "Busy", 1000, 1000);
        rig.F.Regions.Result = new GridRegionList([busy], true);
        Save(rig.F, own with { PlacesSource = FrontPlacesSource.Automatic });
        // The live data is kept a minute; the staff page drops it on every save, as here.
        rig.F.Services.GetRequiredService<GridGate.Launcher.Front.FrontPageSnapshot>().Invalidate();
        page = await PageAsync(Client(rig.F));

        var card = page.QuerySelector("#front-places .front-card")!;
        Assert.Equal("Busy", card.QuerySelector(".front-card-name")!.TextContent.Trim());
        Assert.Equal(FrontApi.MapUrl(busy.Id), card.QuerySelector("img")!.GetAttribute("src"));
        Assert.Null(card.QuerySelector(".front-card-line"));
    }

    // ---- News ----

    [Fact]
    public async Task News_newest_first_without_hidden_and_at_most_five()
    {
        await using var rig = Rig();
        var news = Enumerable.Range(1, 7).Select(d => new SplashNewsItem(new DateOnly(2026, 11, d), En("Day " + d), Hidden: d == 7)).ToList();
        Save(rig.F, FrontPageSettings.Default with { NewsSource = FrontNewsSource.Own, News = news });

        var page = await PageAsync(Client(rig.F));

        Assert.Equal(["Day 6", "Day 5", "Day 4", "Day 3", "Day 2"],
            page.QuerySelectorAll("#front-news article .front-news-text").Select(e => e.TextContent.Trim()));
    }

    [Fact]
    public async Task News_comes_from_the_published_splash_by_default()
    {
        await using var rig = Rig();
        PublishSplash(rig.F, Block(1, SplashBlockKind.News, new(News: [new SplashNewsItem(new DateOnly(2026, 11, 1), En("Grand opening"), false)])));

        var page = await PageAsync(Client(rig.F));

        Assert.Equal(["Grand opening"], page.QuerySelectorAll("#front-news article .front-news-text").Select(e => e.TextContent.Trim()));
    }

    [Fact]
    public async Task Text_falls_back_to_english_then_is_left_out()
    {
        await using var rig = Rig();
        Save(rig.F, FrontPageSettings.Default with
        {
            PlacesSource = FrontPlacesSource.Own,
            Places = [new SplashPlace(Guid.NewGuid(), "Harbour", En("Boats"), null)],
            NewsSource = FrontNewsSource.Own,
            News =
            [
                new SplashNewsItem(new DateOnly(2026, 11, 2), new Dictionary<string, string> { ["fr"] = "Bonjour" }, false),
                new SplashNewsItem(new DateOnly(2026, 11, 1), new Dictionary<string, string> { ["de"] = "Hallo", ["en"] = "Hello" }, false),
            ],
        });

        var page = await PageAsync(Client(rig.F), language: "de");

        Assert.Equal("Boats", page.QuerySelector("#front-places .front-card-line")!.TextContent.Trim());
        Assert.Equal(["Hallo"], page.QuerySelectorAll("#front-news article .front-news-text").Select(e => e.TextContent.Trim()));
    }
}
