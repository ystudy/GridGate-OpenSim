using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Dashboard;
using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Endpoints;
using GridGate.Launcher.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>The Front page staff editor (spec 2026-11-19-front-page-design, "The staff page").</summary>
public sealed class FrontPageEditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Url = "/front-page";

    private static readonly Guid Harbour = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Link = Guid.Parse("99999999-2222-3333-4444-555555555555");

    /// <summary>Robust's map service: a small JPEG for every tile.</summary>
    private sealed class FakeRobustMap : HttpMessageHandler
    {
        public ConcurrentQueue<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(TestPictures.Jpeg(256, 256)) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } },
            });
        }
    }

    private static LauncherFactory Rig(Func<LauncherSettings, LauncherSettings>? settings = null)
    {
        var map = new FakeRobustMap();
        var f = Factory(s => s.AddHttpClient(RegionsApi.RobustMapClient).ConfigurePrimaryHttpMessageHandler(() => map), settings);
        f.Regions.Result = new GridRegionList([new(Harbour, "Harbour", 1000, 1002), new(Link, "Other Grid", 4000, 4000, IsLink: true)], true);
        return f;
    }

    private static FrontPageStore Store(LauncherFactory f) => f.Services.GetRequiredService<FrontPageStore>();

    private static async Task<IDocument> PageAsync(HttpClient client, string url = Url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> PageTokenAsync(HttpClient client) =>
        (await PageAsync(client)).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string handler, Dictionary<string, string> fields, bool token = true)
    {
        if (token) fields["__RequestVerificationToken"] = await PageTokenAsync(client);
        return await client.PostAsync($"{Url}?handler={handler}", new FormUrlEncodedContent(fields), Ct);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string handler, Dictionary<string, string> fields)
    {
        var body = new MultipartFormDataContent();
        foreach (var (k, v) in fields) body.Add(new StringContent(v), k);
        body.Add(new StringContent(await PageTokenAsync(client)), "__RequestVerificationToken");
        var file = new ByteArrayContent(TestPictures.Png(320, 180));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        body.Add(file, "file", "photo.png");
        return await client.PostAsync($"{Url}?handler={handler}", body, Ct);
    }

    private static void Saved(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(Url, r.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("supervisor")]
    [InlineData(null)]
    public async Task Supervisor_and_anonymous_are_refused(string? role)
    {
        await using var f = Rig();
        var client = role is null ? Client(f) : await StaffAsync(f, role);

        using var r = await client.GetAsync(Url, Ct);

        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Admin_sees_the_editor()
    {
        await using var f = Rig();
        var page = await PageAsync(await StaffAsync(f, "admin"));

        Assert.Equal("/home", page.QuerySelector("main a#see-front-page")!.GetAttribute("href"));
        Assert.NotNull(page.QuerySelector("main form#front-photo-form"));
        Assert.NotNull(page.QuerySelector("main form#front-numbers-form"));
    }

    [Fact]
    public async Task Region_server_plan_answers_404()
    {
        await using var f = Rig(s => s with { Plan = OwnerPlan.RegionServer });
        var client = await StaffAsync(f, "owner");

        using var r = await client.GetAsync(Url, Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Uploading_a_photo_shows_it_on_the_front_page()
    {
        await using var f = Rig();
        var admin = await StaffAsync(f, "admin");

        Saved(await UploadAsync(admin, "Photo", []));

        var photo = Store(f).Load().Photo!;
        var front = await PageAsync(Client(f), "/home");
        Assert.Equal(FrontApi.PictureUrl(photo), front.QuerySelector("#front-hero img.front-hero-photo")!.GetAttribute("src"));

        Saved(await PostAsync(admin, "RemovePhoto", []));
        Assert.Null(Store(f).Load().Photo);
    }

    [Fact]
    public async Task Switching_sources_keeps_the_own_list()
    {
        await using var f = Rig();
        var admin = await StaffAsync(f, "admin");
        Saved(await PostAsync(admin, "AddPlace", new() { ["regionId"] = Harbour.ToString() }));
        Saved(await PostAsync(admin, "PlacesSource", new() { ["source"] = "Own" }));
        Saved(await PostAsync(admin, "PlacesSource", new() { ["source"] = "Splash" }));

        Assert.Equal(FrontPlacesSource.Splash, Store(f).Load().PlacesSource);
        Assert.Equal(["Harbour"], Store(f).Load().Places.Select(p => p.RegionName));
        var page = await PageAsync(admin);
        Assert.Equal(["Harbour"], page.QuerySelectorAll("#front-own-places .front-own-place-name").Select(e => e.TextContent.Trim()));
    }

    [Fact]
    public async Task Add_place_refuses_a_hypergrid_link_and_an_unknown_region()
    {
        await using var f = Rig();
        var admin = await StaffAsync(f, "admin");

        using var link = await PostAsync(admin, "AddPlace", new() { ["regionId"] = Link.ToString() });
        using var unknown = await PostAsync(admin, "AddPlace", new() { ["regionId"] = Guid.NewGuid().ToString() });

        Assert.Empty(Store(f).Load().Places);
        // The page offers only the grid's own regions.
        var options = (await PageAsync(admin)).QuerySelectorAll("#front-add-place select[name=regionId] option").Select(o => o.GetAttribute("value"));
        Assert.Equal([Harbour.ToString()], options);
    }

    [Fact]
    public async Task Saving_a_place_keeps_its_lines_and_a_map_picture()
    {
        await using var f = Rig();
        var admin = await StaffAsync(f, "admin");
        Saved(await PostAsync(admin, "AddPlace", new() { ["regionId"] = Harbour.ToString() }));

        Saved(await PostAsync(admin, "SavePlace", new() { ["index"] = "0", ["regionId"] = Harbour.ToString(), ["line[en]"] = "Boats and sunsets", ["line[de]"] = "Boote", ["fromMap"] = "true" }));

        var place = Assert.Single(Store(f).Load().Places);
        Assert.Equal("Boats and sunsets", place.Line["en"]);
        Assert.Equal("Boote", place.Line["de"]);
        Assert.NotNull(place.Picture);
        Assert.True(f.Services.GetRequiredKeyedService<PictureStore>("front").Exists(place.Picture!));
    }

    [Fact]
    public async Task Move_and_remove_places()
    {
        await using var f = Rig();
        f.Regions.Result = new GridRegionList([new(Harbour, "Harbour", 1000, 1002), new(Guid.NewGuid(), "Hills", 1001, 1002)], true);
        var admin = await StaffAsync(f, "admin");
        foreach (var r in f.Regions.Result.Regions)
            Saved(await PostAsync(admin, "AddPlace", new() { ["regionId"] = r.Id.ToString() }));
        var hills = f.Regions.Result.Regions[1].Id;

        Saved(await PostAsync(admin, "MovePlace", new() { ["index"] = "1", ["regionId"] = hills.ToString(), ["by"] = "-1" }));
        Assert.Equal(["Hills", "Harbour"], Store(f).Load().Places.Select(p => p.RegionName));

        Saved(await PostAsync(admin, "RemovePlace", new() { ["index"] = "0", ["regionId"] = hills.ToString() }));
        Assert.Equal(["Harbour"], Store(f).Load().Places.Select(p => p.RegionName));
    }

    [Fact]
    public async Task Add_hide_and_remove_news()
    {
        await using var f = Rig();
        var admin = await StaffAsync(f, "admin");

        Saved(await PostAsync(admin, "AddNews", new() { ["date"] = "2026-11-19", ["text[en]"] = "Opening night" }));
        var item = Assert.Single(Store(f).Load().News);
        Assert.Equal((new DateOnly(2026, 11, 19), "Opening night", false), (item.Date, item.Text["en"], item.Hidden));

        Saved(await PostAsync(admin, "ToggleNews", new() { ["index"] = "0", ["key"] = Pages.FrontPage.IndexModel.NewsKey(item) }));
        Assert.True(Store(f).Load().News[0].Hidden);

        Saved(await PostAsync(admin, "NewsSource", new() { ["source"] = "Own" }));
        Assert.Equal(FrontNewsSource.Own, Store(f).Load().NewsSource);

        Saved(await PostAsync(admin, "RemoveNews", new() { ["index"] = "0", ["key"] = Pages.FrontPage.IndexModel.NewsKey(Store(f).Load().News[0]) }));
        Assert.Empty(Store(f).Load().News);
    }

    [Fact]
    public async Task Saving_the_numbers_shows_at_once()
    {
        await using var f = Rig();
        f.Residents.Counts = new ResidentCounts(1204, 87);
        var admin = await StaffAsync(f, "admin");
        Assert.NotNull((await PageAsync(Client(f), "/home")).QuerySelector("#front-numbers")); // builds and keeps the live data

        Saved(await PostAsync(admin, "Numbers", new() { ["registered"] = "true" })); // only Registered stays on

        var tiles = (await PageAsync(Client(f), "/home")).QuerySelectorAll("#front-numbers .front-number-label").Select(e => e.TextContent.Trim());
        Assert.Equal(["Registered avatars"], tiles);
    }

    [Fact]
    public async Task A_stale_tab_changes_nothing()
    {
        // Two tabs: the first still shows Harbour, Hills, Club; the second has already removed Harbour. The first tab's Remove
        // on "Hills" (its index 1) now points at Club, so it must change nothing rather than remove the wrong place.
        await using var f = Rig();
        var ids = new[] { Harbour, Guid.NewGuid(), Guid.NewGuid() };
        f.Regions.Result = new GridRegionList([new(ids[0], "Harbour", 1000, 1002), new(ids[1], "Hills", 1001, 1002), new(ids[2], "Club", 1002, 1002)], true);
        var admin = await StaffAsync(f, "admin");
        foreach (var id in ids)
            Saved(await PostAsync(admin, "AddPlace", new() { ["regionId"] = id.ToString() }));
        Saved(await PostAsync(admin, "RemovePlace", new() { ["index"] = "0", ["regionId"] = ids[0].ToString() })); // the other tab

        using var r = await PostAsync(admin, "RemovePlace", new() { ["index"] = "1", ["regionId"] = ids[1].ToString() });
        using var move = await PostAsync(admin, "MovePlace", new() { ["index"] = "2", ["regionId"] = ids[2].ToString(), ["by"] = "-1" });

        Assert.Equal(["Hills", "Club"], Store(f).Load().Places.Select(p => p.RegionName));
        Assert.Contains("changed in another window", (await PageAsync(admin)).QuerySelector("main .alert-danger")!.TextContent);
    }

    [Fact]
    public async Task A_stale_news_action_changes_nothing()
    {
        await using var f = Rig();
        var admin = await StaffAsync(f, "admin");
        Saved(await PostAsync(admin, "AddNews", new() { ["date"] = "2026-11-18", ["text[en]"] = "First" }));
        Saved(await PostAsync(admin, "AddNews", new() { ["date"] = "2026-11-19", ["text[en]"] = "Second" }));
        var first = Store(f).Load().News[0];
        Saved(await PostAsync(admin, "RemoveNews", new() { ["index"] = "0", ["key"] = Pages.FrontPage.IndexModel.NewsKey(first) }));

        using var r = await PostAsync(admin, "ToggleNews", new() { ["index"] = "0", ["key"] = Pages.FrontPage.IndexModel.NewsKey(first) });

        Assert.False(Assert.Single(Store(f).Load().News).Hidden); // "Second" untouched
    }

    [Fact]
    public async Task Posts_without_a_token_are_refused()
    {
        await using var f = Rig();
        var admin = await StaffAsync(f, "admin");

        using var r = await PostAsync(admin, "PlacesSource", new() { ["source"] = "Automatic" }, token: false);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(FrontPlacesSource.Splash, Store(f).Load().PlacesSource);
    }

    [Fact]
    public void Menu_lists_front_page_after_splash_page()
    {
        var settings = TestSettings.Create() with { Plan = OwnerPlan.Local };
        foreach (var role in new[] { Core.Security.StaffRole.Owner, Core.Security.StaffRole.Admin })
        {
            var guest = StaffMenu.For(role, settings, canApprove: false).Single(g => g.Text == "Guest pages");
            var texts = guest.Items.Select(i => i.Text).ToList();
            Assert.Equal(texts.IndexOf("Splash page") + 1, texts.IndexOf("Front page"));
            Assert.Equal("/front-page", guest.Items.Single(i => i.Text == "Front page").Href);
        }
    }

    [Fact]
    public async Task The_hub_explains_the_front_page()
    {
        await using var f = Rig();
        var page = await PageAsync(await StaffAsync(f, "owner"), "/guest-pages");

        Assert.NotNull(page.QuerySelector("main a[href='/front-page']"));
    }
}
