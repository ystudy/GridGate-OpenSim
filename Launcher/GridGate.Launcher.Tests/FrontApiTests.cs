using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Splash;
using GridGate.Launcher.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static GridGate.Launcher.Tests.SplashPictureApiTests;

namespace GridGate.Launcher.Tests;

/// <summary>
/// What anyone may fetch from the front page (spec 2026-11-19-front-page-design, Constraints): its pictures only while the page
/// shows them, and a region's map tile only while that region is on the Automatic list.
/// </summary>
public sealed class FrontApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Robust's map service: answers a small JPEG for every tile and records what was asked.</summary>
    private sealed class FakeRobustMap : HttpMessageHandler
    {
        public ConcurrentQueue<string> Asked { get; } = new();

        /// <summary>The grid has no tiles yet: every tile is a 404.</summary>
        public bool Missing { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Enqueue(request.RequestUri!.AbsolutePath);
            if (Missing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(TestPictures.Jpeg(256, 256)) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } },
            });
        }
    }

    private static LauncherFactory WithMap(FakeRobustMap map, Func<Core.Settings.LauncherSettings, Core.Settings.LauncherSettings>? settings = null) =>
        Factory(s => s.AddHttpClient(RegionsApi.RobustMapClient).ConfigurePrimaryHttpMessageHandler(() => map), settings);

    private static async Task<string> AddAsync(LauncherFactory f) =>
        Assert.IsType<PictureResult.Added>(await f.Services.GetRequiredKeyedService<PictureStore>("front")
            .AddAsync(new MemoryStream(TestPictures.Png(64, 64)), "p.png", "Test Owner", Ct)).Picture.Name;

    private static void Save(LauncherFactory f, FrontPageSettings s) => Assert.Empty(f.Services.GetRequiredService<FrontPageStore>().Save(s));

    private static async Task<HttpStatusCode> GetAsync(LauncherFactory f, string url)
    {
        using var r = await Client(f).GetAsync(url, Ct);
        return r.StatusCode;
    }

    private static SplashPlace Place(string picture) => new(Guid.NewGuid(), "Harbour", new Dictionary<string, string> { ["en"] = "Boats" }, picture);

    [Fact]
    public async Task Photo_is_served_to_anyone()
    {
        await using var f = Factory();
        var photo = await AddAsync(f);
        Save(f, FrontPageSettings.Default with { Photo = photo });

        using var r = await Client(f).GetAsync(FrontApi.PictureUrl(photo), Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("immutable", r.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Front_pictures_reachable_outside_the_staff_networks()
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            // Every caller comes from outside the network list set below.
            Before = app =>
            {
                app.Use((ctx, next) =>
                {
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
                    return next(ctx);
                });
                TestAuth.Apply(app);
            },
        };
        await using var _ = f;
        TestAuth.AddStaff(f.Robust);
        var photo = await AddAsync(f);
        Save(f, FrontPageSettings.Default with { Photo = photo });
        f.Store.Save(f.Store.LoadOrCreate() with { Security = f.Store.LoadOrCreate().Security with { AdminNetworks = ["10.0.0.0/8"] } });

        Assert.Equal(HttpStatusCode.OK, await GetAsync(f, FrontApi.PictureUrl(photo)));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, "/login"));
    }

    [Fact]
    public async Task Own_place_picture_is_served_only_while_the_source_is_own()
    {
        await using var f = Factory();
        var pic = await AddAsync(f);
        var own = FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Own, Places = [Place(pic)] };

        Save(f, own);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(f, FrontApi.PictureUrl(pic)));
        Save(f, own with { PlacesSource = FrontPlacesSource.Splash });
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.PictureUrl(pic)));
        Save(f, own);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(f, FrontApi.PictureUrl(pic)));
    }

    [Fact]
    public async Task A_picture_the_page_does_not_use_is_404()
    {
        await using var f = Factory();
        var unused = await AddAsync(f);

        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.PictureUrl(unused)));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, "/front/images/not-a-name.png"));
    }

    [Fact]
    public async Task Map_tile_is_served_for_a_busiest_region_while_automatic()
    {
        var map = new FakeRobustMap();
        await using var f = WithMap(map);
        var harbour = new GridRegion(Guid.NewGuid(), "Harbour", 1000, 1002);
        f.Regions.Result = new GridRegionList([harbour], true);
        Save(f, FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Automatic });

        using var r = await Client(f).GetAsync(FrontApi.MapUrl(harbour.Id), Ct);
        using var again = await Client(f).GetAsync(FrontApi.MapUrl(harbour.Id), Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/jpeg", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["/map-1-1000-1002-objects.jpg"], map.Asked); // the second visitor got the cached tile
    }

    [Fact]
    public async Task Map_tile_is_404_for_a_link_a_region_outside_the_six_or_another_source()
    {
        var map = new FakeRobustMap();
        await using var f = WithMap(map);
        var link = new GridRegion(Guid.NewGuid(), "Elsewhere", 4000, 4000, IsLink: true);
        var regions = Enumerable.Range(0, 7).Select(i => new GridRegion(Guid.NewGuid(), "Region " + i, 1000 + i, 1000)).ToList();
        f.Regions.Result = new GridRegionList([.. regions, link], true);
        Save(f, FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Automatic });

        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.MapUrl(link.Id)));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.MapUrl(regions[6].Id))); // "Region 6" is seventh by name
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.MapUrl(Guid.NewGuid())));
        Save(f, FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Splash });
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.MapUrl(regions[0].Id)));

        Assert.Empty(map.Asked);
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task A_machine_without_a_public_side_answers_404_for_both(OwnerPlan plan)
    {
        var map = new FakeRobustMap();
        await using var f = WithMap(map, s => s with { Plan = plan });
        var photo = await AddAsync(f);
        var harbour = new GridRegion(Guid.NewGuid(), "Harbour", 1000, 1002);
        f.Regions.Result = new GridRegionList([harbour], true);
        Save(f, FrontPageSettings.Default with { Photo = photo, PlacesSource = FrontPlacesSource.Automatic });

        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.PictureUrl(photo)));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.MapUrl(harbour.Id)));
        Assert.Empty(map.Asked);
    }

    [Fact]
    public async Task A_region_without_a_map_tile_gets_the_blank_card()
    {
        var map = new FakeRobustMap { Missing = true };
        await using var f = WithMap(map);
        var harbour = new GridRegion(Guid.NewGuid(), "Harbour", 1000, 1002);
        f.Regions.Result = new GridRegionList([harbour], true);
        Save(f, FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Automatic });
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.MapUrl(harbour.Id)));

        var page = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(await Client(f).GetStringAsync("/home", Ct));

        var card = page.QuerySelector("#front-places .front-card")!;
        Assert.Equal("Harbour", card.QuerySelector(".front-card-name")!.TextContent.Trim());
        Assert.Null(card.QuerySelector("img")); // no request for a picture the grid doesn't have
        Assert.NotNull(card.QuerySelector(".front-card-blank"));
        Assert.Single(map.Asked); // the page itself asked nothing more
    }

    [Fact]
    public void A_card_picture_that_fails_to_load_shows_the_blank_gradient()
    {
        // The first visitor after a tile expires still gets its address; should the tile be gone, the box behind it is the
        // blank card's gradient, not a broken picture.
        var css = GuestCss();
        var picture = Regex.Match(css, @"\.front-card-picture\s*\{([^}]*)\}").Groups[1].Value;
        var blank = Regex.Match(css, @"\.front-card-blank\s*\{\s*background:\s*([^;]*);").Groups[1].Value;

        Assert.NotEqual("", blank);
        Assert.Contains("background: " + blank, picture);
    }

    private static string GuestCss()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("GridGate.sln not found");
        return File.ReadAllText(Path.Combine(root, "Launcher", "GridGate.Launcher", "wwwroot", "css", "guest.css"));
    }

    // ---- The editor's preview of any front picture ----

    [Fact]
    public async Task Staff_preview_serves_a_picture_the_page_does_not_show()
    {
        await using var f = Factory();
        var pic = await AddAsync(f);
        Save(f, FrontPageSettings.Default with { PlacesSource = FrontPlacesSource.Splash, Places = [Place(pic)] });

        using var r = await (await StaffAsync(f, "admin")).GetAsync(FrontApi.StaffPictureUrl(pic), Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/png", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(f, FrontApi.PictureUrl(pic))); // still not public
    }

    [Theory]
    [InlineData("supervisor")]
    [InlineData(null)]
    public async Task Staff_preview_refuses_supervisor_and_anonymous(string? role)
    {
        await using var f = Factory();
        var pic = await AddAsync(f);
        var client = role is null ? Client(f) : await StaffAsync(f, role);

        using var r = await client.GetAsync(FrontApi.StaffPictureUrl(pic), Ct);

        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Staff_preview_is_404_without_a_public_side_or_a_picture()
    {
        await using (var f = Factory(settings: s => s with { Plan = OwnerPlan.RegionServer }))
        {
            var pic = await AddAsync(f);
            using var r = await (await StaffAsync(f, "owner")).GetAsync(FrontApi.StaffPictureUrl(pic), Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        await using var g = Factory();
        using var none = await (await StaffAsync(g, "owner")).GetAsync(FrontApi.StaffPictureUrl("0123456789abcdef0123456789abcdef.png"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }
}
