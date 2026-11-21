using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Endpoints;
using GridGate.Launcher.Featured;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The public Featured page (spec 2026-11-21-featured-places-design): listed places with Teleport links for anyone, Adult
/// places only for those signed in, the stats row without zeros, the empty and unavailable states, and its map pictures
/// only for regions the page shows.
/// </summary>
public sealed class FeaturedPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly GridRegion Bay = FeaturedQueryTests.Bay;
    private static readonly GridRegion Club = FeaturedQueryTests.Club;

    private static LauncherFactory Factory()
    {
        var f = WorldMapApiTests.Factory();
        f.Regions.Result = new GridRegionList([Bay, Club], true);
        return f;
    }

    private static void List(LauncherFactory f, params ListedParcel[] parcels) => f.Listed.Result = new ListedParcelList(parcels, true);

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url = "/featured")
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)r.StatusCode}");
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static List<string> Names(IHtmlDocument page) =>
        [.. page.QuerySelectorAll("#featured-places .front-card-name").Select(n => n.TextContent.Trim())];

    [Fact]
    public async Task Visitor_sees_listed_places_with_teleport()
    {
        await using var f = Factory();
        List(f,
            FeaturedQueryTests.Parcel(1, Bay, "Beach shop", category: 11, visits: 213) with { HasLandingPoint = true, X = 128.6f, Y = 64.2f, Z = 25.9f },
            FeaturedQueryTests.Parcel(2, Bay, "Quiet garden", category: 9, visits: 1));
        var start = WorldMapApi.TeleportStart(f.Store.LoadOrCreate());

        var page = await PageAsync(WorldMapApiTests.Client(f));

        Assert.Equal("Featured places", page.QuerySelector("main h1")!.TextContent.Trim());
        Assert.Equal(["Beach shop", "Quiet garden"], Names(page));
        var card = page.QuerySelector("#featured-places .front-card")!;
        Assert.Contains("Sunny Bay", card.QuerySelector(".featured-where")!.TextContent);
        Assert.Contains("Shopping", card.QuerySelector(".featured-where")!.TextContent);
        Assert.Equal("213 recent visits", card.QuerySelector(".featured-visits")!.TextContent.Trim());
        Assert.Equal(start + "Sunny%20Bay/128/64/25", card.QuerySelector("a.featured-teleport")!.GetAttribute("href"));
        Assert.Equal("/featured/picture/" + FeaturedQueryTests.Parcel(1, Bay, "").Id + ".jpg", card.QuerySelector("img.front-card-picture")!.GetAttribute("src"));
        var second = page.QuerySelectorAll("#featured-places .front-card")[1];
        Assert.Equal(start + "Sunny%20Bay", second.QuerySelector("a.featured-teleport")!.GetAttribute("href"));
        Assert.Equal("1 recent visit", second.QuerySelector(".featured-visits")!.TextContent.Trim());
    }

    [Fact]
    public async Task Stats_row_and_hidden_zero_tiles()
    {
        await using var f = Factory();
        List(f, FeaturedQueryTests.Parcel(1, Bay, "Beach shop", category: 0));

        var page = await PageAsync(WorldMapApiTests.Client(f));

        var labels = page.QuerySelectorAll("#featured-numbers .front-number-label").Select(l => l.TextContent.Trim()).ToList();
        Assert.Equal(["Places", "Regions"], labels); // no visits yet and no category: those tiles are hidden
    }

    [Fact]
    public async Task Adult_place_hidden_from_visitor_but_shown_to_resident()
    {
        await using var f = Factory();
        List(f,
            FeaturedQueryTests.Parcel(1, Bay, "Beach shop"),
            FeaturedQueryTests.Parcel(2, Club, "Red room", category: 2, rating: 2, visits: 40));

        var visitor = await PageAsync(WorldMapApiTests.Client(f));
        var resident = await PageAsync(await WorldMapApiTests.ResidentAsync(f));

        Assert.Equal(["Beach shop"], Names(visitor));
        Assert.DoesNotContain("Red room", visitor.Body!.TextContent);
        Assert.DoesNotContain(Club.Id.ToString(), visitor.Body!.InnerHtml);
        Assert.Null(visitor.QuerySelector("select[name=rating]"));
        Assert.Equal(["Red room", "Beach shop"], Names(resident));
        Assert.NotNull(resident.QuerySelector("select[name=rating]"));
        Assert.Equal("Adult", resident.QuerySelector("#featured-places .featured-rating")!.TextContent.Trim());
    }

    [Fact]
    public async Task Picture_only_for_shown_places()
    {
        await using var f = Factory();
        var adult = FeaturedQueryTests.Parcel(2, Club, "Red room", category: 2, rating: 2);
        List(f, adult);
        var visitor = WorldMapApiTests.Client(f);

        using var adultOnly = await visitor.GetAsync($"/featured/picture/{adult.Id}.jpg", Ct);
        using var unknown = await visitor.GetAsync($"/featured/picture/{Guid.NewGuid()}.jpg", Ct);
        using var oldRoute = await visitor.GetAsync($"/featured/map/{Club.Id}.jpg", Ct);

        Assert.Equal(HttpStatusCode.NotFound, adultOnly.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, oldRoute.StatusCode); // the old map route is gone (an unknown address goes to sign-in)
    }

    /// <summary>Robust's asset service and converter, answering one snapshot.</summary>
    private sealed class SnapshotRobust(Guid snapshot) : HttpMessageHandler
    {
        public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 7, 7];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"/assets/{snapshot}/data")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0x4F, 0xFF, 0x51]) });
            if (request.Method == HttpMethod.Post && path == "/gridgate/j2k")
            {
                var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) };
                answer.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                return Task.FromResult(answer);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Picture_is_the_parcels_own_snapshot()
    {
        var snapshot = Guid.Parse("0d000000-0000-0000-0000-0000000000b2");
        await using var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services => services.AddHttpClient(ParcelPictures.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new SnapshotRobust(snapshot)),
        };
        f.Regions.Result = new GridRegionList([Bay], true);
        var shop = FeaturedQueryTests.Parcel(1, Bay, "Beach shop") with { SnapshotId = snapshot };
        List(f, shop);

        using var r = await WorldMapApiTests.Client(f).GetAsync($"/featured/picture/{shop.Id}.jpg", Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/jpeg", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal(SnapshotRobust.Jpeg, await r.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("public, max-age=300", r.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Empty_directory_says_how_to_list()
    {
        await using var f = Factory();

        var page = await PageAsync(WorldMapApiTests.Client(f));

        Assert.Contains("Show in search", page.QuerySelector("#featured-none")!.TextContent);
        Assert.Null(page.QuerySelector("#featured-numbers"));
        Assert.Null(page.QuerySelector("#featured-filter"));
    }

    [Fact]
    public async Task Unavailable_says_so()
    {
        await using var f = Factory();
        f.Listed.Result = ListedParcelList.Unavailable;

        var page = await PageAsync(WorldMapApiTests.Client(f));

        Assert.NotNull(page.QuerySelector("#featured-unavailable"));
        Assert.Null(page.QuerySelector("#featured-places"));
    }

    [Fact]
    public async Task Odd_query_strings_give_a_page()
    {
        await using var f = Factory();
        List(f, FeaturedQueryTests.Parcel(1, Bay, "Beach shop"));

        var page = await PageAsync(WorldMapApiTests.Client(f), "/featured?page=abc&category=99&rating=-1&sort=up&q=" + new string('z', 5000));

        Assert.NotNull(page.QuerySelector("#featured-no-match"));
        Assert.Equal(100, page.QuerySelector("input[name=q]")!.GetAttribute("value")!.Length);
    }

    [Fact]
    public async Task Description_is_text_not_html()
    {
        await using var f = Factory();
        List(f, FeaturedQueryTests.Parcel(1, Bay, "<b>Bold</b> shop", description: "<script>alert(1)</script> sale"));

        var page = await PageAsync(WorldMapApiTests.Client(f));

        Assert.Equal("<b>Bold</b> shop", Names(page).Single());
        Assert.Null(page.QuerySelector("#featured-places b"));
        Assert.Null(page.QuerySelector("#featured-places script"));
        Assert.Contains("<script>alert(1)</script> sale", page.QuerySelector(".featured-description")!.TextContent);
    }

    [Fact]
    public async Task Search_category_and_paging_links_keep_the_choices()
    {
        await using var f = Factory();
        List(f, [.. Enumerable.Range(1, 30).Select(i => FeaturedQueryTests.Parcel(i, Bay, $"Shop {i:D2}", category: 11))]);

        var page = await PageAsync(WorldMapApiTests.Client(f), "/featured?q=shop&category=11&sort=name");

        Assert.Equal(24, Names(page).Count);
        Assert.Equal("Shop 01", Names(page)[0]);
        Assert.Equal("/featured?q=shop&category=11&sort=name&page=2", page.QuerySelector("a#featured-next")!.GetAttribute("href"));
        Assert.Null(page.QuerySelector("a#featured-previous"));
        Assert.Equal("11", page.QuerySelector("select[name=category] option[selected]")!.GetAttribute("value"));
    }
}
