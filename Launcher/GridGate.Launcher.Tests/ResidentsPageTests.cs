using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Endpoints;
using GridGate.Launcher.Residents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The public Residents pages (spec 2026-11-21-resident-profiles-design): only residents who chose it, for anyone; the profile
/// page and its 404 for everyone else; the pictures only for what the pages show; the Account page switch; the staff hold.
/// All names are made up.
/// </summary>
public sealed class ResidentsPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid AnnId = Guid.Parse("a0000000-0000-0000-0000-0000000000b1");
    private static readonly Guid BobId = Guid.Parse("a0000000-0000-0000-0000-0000000000b2");
    private static readonly Guid AnnPhoto = Guid.Parse("a0000000-0000-0000-0000-0000000000c1");
    private static readonly Guid PickId = Guid.Parse("a0000000-0000-0000-0000-0000000000d1");
    private const string AnnPassword = "ann-password-1";

    private static readonly PublicPick ShopPick = new(PickId, "My shop", "Hats and boats.", Guid.Parse("a0000000-0000-0000-0000-0000000000d2"),
        "Sandbox", 1002 * 256 + 100.7f, 1000 * 256 + 50.2f, 23.9f);

    private static PublicProfile Ann => new(AnnId, "Ann", "Sailor", new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero), AnnPhoto,
        "I build boats.\nCome sailing!", "Sail every day", "Building, scripting", "English, Deutsch", "javascript:alert(1)", [ShopPick]);

    private static PublicProfile Bob => new(BobId, "Bob", "Builder", new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), Guid.Empty,
        "", "", "", "", "example.com/bob", []);

    private static LauncherFactory Factory()
    {
        var f = WorldMapApiTests.Factory();
        f.Profiles.Add(Ann);
        f.Profiles.Add(Bob);
        f.Robust.Add("Ann", "Sailor", 0, AnnPassword, AnnId, email: "ann@example.com");
        return f;
    }

    private static PublicProfileChoices Choices(LauncherFactory f) => f.Services.GetRequiredService<PublicProfileChoices>();

    private static void Show(LauncherFactory f, params Guid[] ids)
    {
        foreach (var id in ids) Choices(f).Choose(id, true);
        f.Services.GetRequiredService<ResidentDirectory>().Invalidate();
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient client, string url)
    {
        using var r = await client.GetAsync(url, Ct);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)r.StatusCode}");
        return new HtmlParser().ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static List<string> Names(IHtmlDocument page) =>
        [.. page.QuerySelectorAll("#residents-list .front-card-name").Select(n => n.TextContent.Trim())];

    [Fact]
    public async Task Nobody_is_listed_until_they_choose_it()
    {
        await using var f = Factory();
        var page = await PageAsync(WorldMapApiTests.Client(f), "/residents");

        Assert.Equal("Residents", page.QuerySelector("main h1")!.TextContent.Trim());
        Assert.NotNull(page.QuerySelector("#residents-none"));
        Assert.Equal("/guest/account#public-profile", page.QuerySelector("a#residents-add-me")!.GetAttribute("href"));
        Assert.Empty(Names(page));
        Assert.Empty(f.Profiles.LastAsked);
        Assert.Equal("noindex", page.QuerySelector("meta[name=robots]")!.GetAttribute("content"));
    }

    [Fact]
    public async Task Visitor_sees_the_residents_who_chose_it_newest_first()
    {
        await using var f = Factory();
        Show(f, AnnId, BobId);

        var page = await PageAsync(WorldMapApiTests.Client(f), "/residents");

        Assert.Equal(["Bob Builder", "Ann Sailor"], Names(page));
        var ann = page.QuerySelectorAll("#residents-list .resident-card")[1];
        Assert.Equal("/residents/Ann/Sailor", ann.QuerySelector("a.resident-card-link")!.GetAttribute("href"));
        Assert.Equal($"/residents/picture/{AnnId}.jpg", ann.QuerySelector("img")!.GetAttribute("src"));
        Assert.Equal("Member since March 2026", ann.QuerySelector(".resident-since")!.TextContent.Trim());
        Assert.Equal("I build boats. Come sailing!", ann.QuerySelector(".resident-about")!.TextContent.Trim());
        // Bob has no picture: a plain card, and no picture asked for.
        Assert.Null(page.QuerySelectorAll("#residents-list .resident-card")[0].QuerySelector("img"));

        var labels = page.QuerySelectorAll("#residents-numbers .front-number-label").Select(l => l.TextContent.Trim()).ToList();
        Assert.Contains("On this page", labels);
        Assert.NotNull(page.QuerySelector("#menu-residents"));
    }

    [Fact]
    public async Task Search_and_sort_by_name()
    {
        await using var f = Factory();
        Show(f, AnnId, BobId);
        var client = WorldMapApiTests.Client(f);

        Assert.Equal(["Ann Sailor", "Bob Builder"], Names(await PageAsync(client, "/residents?sort=name")));
        Assert.Equal(["Ann Sailor"], Names(await PageAsync(client, "/residents?q=boats")));
        var none = await PageAsync(client, "/residents?q=nobody");
        Assert.NotNull(none.QuerySelector("#residents-no-match"));
    }

    [Fact]
    public async Task Unavailable_when_the_profiles_cannot_be_read()
    {
        await using var f = Factory();
        Show(f, AnnId);
        f.Profiles.Down = true;
        f.Services.GetRequiredService<ResidentDirectory>().Invalidate();

        var page = await PageAsync(WorldMapApiTests.Client(f), "/residents");
        Assert.NotNull(page.QuerySelector("#residents-unavailable"));
    }

    [Fact]
    public async Task Profile_page_shows_what_they_filled_in_as_text()
    {
        await using var f = Factory();
        Show(f, AnnId);
        var start = WorldMapApi.TeleportStart(f.Store.LoadOrCreate());

        var page = await PageAsync(WorldMapApiTests.Client(f), "/residents/ann/SAILOR");

        Assert.Equal("Ann Sailor", page.QuerySelector("main h1")!.TextContent.Trim());
        Assert.Equal("I build boats.\nCome sailing!", page.QuerySelector("#resident-about")!.TextContent);
        var details = page.QuerySelectorAll("#resident-details dt").Select(d => d.TextContent.Trim()).ToList();
        Assert.Equal(["I want to", "Skills", "Languages"], details); // the javascript: link is left out
        Assert.Null(page.QuerySelector("#resident-web"));
        Assert.Equal($"/residents/picture/{AnnId}.jpg", page.QuerySelector("img.resident-photo")!.GetAttribute("src"));

        var pick = page.QuerySelector("#resident-picks .resident-pick")!;
        Assert.Equal("My shop", pick.QuerySelector(".front-card-name")!.TextContent.Trim());
        Assert.Equal($"/residents/pick/{PickId}.jpg", pick.QuerySelector("img")!.GetAttribute("src"));
        Assert.Equal(start + "Sandbox/100/50/23", pick.QuerySelector("a.featured-teleport")!.GetAttribute("href"));
        Assert.Equal("noindex", page.QuerySelector("meta[name=robots]")!.GetAttribute("content"));
    }

    [Fact]
    public async Task Web_link_is_https_and_marked_as_theirs()
    {
        await using var f = Factory();
        Show(f, BobId);

        var page = await PageAsync(WorldMapApiTests.Client(f), "/residents/Bob/Builder");

        var link = page.QuerySelector("a#resident-web")!;
        Assert.Equal("https://example.com/bob", link.GetAttribute("href"));
        Assert.Equal("nofollow ugc noopener noreferrer", link.GetAttribute("rel"));
        Assert.Null(page.QuerySelector("#resident-picks"));
    }

    [Fact]
    public async Task A_pick_on_a_region_that_is_not_online_has_no_teleport()
    {
        await using var f = Factory();
        Show(f, AnnId);
        f.Regions.Result = new GridRegionList([], true);
        f.Services.GetRequiredService<ResidentDirectory>().Invalidate();

        var page = await PageAsync(WorldMapApiTests.Client(f), "/residents/Ann/Sailor");
        Assert.NotNull(page.QuerySelector("#resident-picks .resident-pick"));
        Assert.Null(page.QuerySelector("#resident-picks a.featured-teleport"));
    }

    [Theory]
    [InlineData("/residents/Bob/Builder")] // not shown
    [InlineData("/residents/Nobody/Here")] // no such resident
    [InlineData("/residents/picture/a0000000-0000-0000-0000-0000000000b2.jpg")] // Bob's picture: not shown
    [InlineData("/residents/pick/a0000000-0000-0000-0000-0000000000d9.jpg")] // no such pick
    public async Task Anyone_not_shown_is_404(string url)
    {
        await using var f = Factory();
        Show(f, AnnId);
        using var r = await WorldMapApiTests.Client(f).GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Pictures_are_the_profile_picture_and_the_picks_snapshot()
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
                .ConfigurePrimaryHttpMessageHandler(() => new TwoTextures(AnnPhoto, ShopPick.SnapshotId)),
        };
        f.Profiles.Add(Ann);
        Show(f, AnnId);
        var client = WorldMapApiTests.Client(f);

        using var photo = await client.GetAsync($"/residents/picture/{AnnId}.jpg", Ct);
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal("image/jpeg", photo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FeaturedPageTests.SnapshotRobust.Jpeg, await photo.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal("public, max-age=300", photo.Headers.CacheControl?.ToString());

        using var pick = await client.GetAsync($"/residents/pick/{PickId}.jpg", Ct);
        Assert.Equal(HttpStatusCode.OK, pick.StatusCode);

        // Off the page: the pictures go too (once the shared directory is rebuilt, here at once).
        Choices(f).Choose(AnnId, false);
        f.Services.GetRequiredService<ResidentDirectory>().Invalidate();
        using var gone = await client.GetAsync($"/residents/picture/{AnnId}.jpg", Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    /// <summary>Robust answering two textures, each through <see cref="FeaturedPageTests.SnapshotRobust"/>.</summary>
    private sealed class TwoTextures(Guid one, Guid two) : DelegatingHandler
    {
        private readonly HttpMessageInvoker _one = new(new FeaturedPageTests.SnapshotRobust(one));
        private readonly HttpMessageInvoker _two = new(new FeaturedPageTests.SnapshotRobust(two));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var first = await _one.SendAsync(Copy(request), ct);
            return first.IsSuccessStatusCode ? first : await _two.SendAsync(request, ct);
        }

        private static HttpRequestMessage Copy(HttpRequestMessage r) => new(r.Method, r.RequestUri) { Content = r.Content };
    }

    [Fact]
    public async Task Not_there_on_a_region_server()
    {
        await using var f = WorldMapApiTests.Factory(s => s with { Plan = OwnerPlan.RegionServer });
        using var r = await WorldMapApiTests.Client(f).GetAsync("/residents", Ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        using var picture = await WorldMapApiTests.Client(f).GetAsync($"/residents/picture/{AnnId}.jpg", Ct);
        Assert.Equal(HttpStatusCode.NotFound, picture.StatusCode);
    }

    [Fact]
    public async Task Open_to_every_address()
    {
        await using var f = WorldMapApiTests.Factory(outside: true);
        using var r = await WorldMapApiTests.Client(f).GetAsync("/residents", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // ---- The Account page switch ----

    private static async Task<HttpClient> AnnSignedInAsync(LauncherFactory f)
    {
        var client = WorldMapApiTests.Client(f);
        var page = await PageAsync(client, "/guest/sign-in");
        using var r = await client.PostAsync("/guest/sign-in", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Ann Sailor", ["Password"] = AnnPassword, ["__RequestVerificationToken"] = Token(page, "form"),
        }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return client;
    }

    private static string Token(IHtmlDocument page, string form) =>
        page.QuerySelector(form + " input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

    private static async Task<HttpResponseMessage> SwitchAsync(HttpClient client, bool shown)
    {
        var page = await PageAsync(client, "/guest/account");
        var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(page, "form#change-public-profile") };
        if (shown) fields["shown"] = "true";
        return await client.PostAsync("/guest/account?handler=PublicProfile", new FormUrlEncodedContent(fields), Ct);
    }

    [Fact]
    public async Task A_resident_turns_their_page_on_and_off_from_their_account()
    {
        await using var f = Factory();
        var client = await AnnSignedInAsync(f);

        var before = await PageAsync(client, "/guest/account");
        Assert.False(before.QuerySelector("#public-profile-on")!.HasAttribute("checked"));
        Assert.Null(before.QuerySelector("#public-profile-see"));

        using (var on = await SwitchAsync(client, true))
            Assert.Equal(HttpStatusCode.Redirect, on.StatusCode);
        Assert.Equal(PublicProfileState.On, Choices(f).StateOf(AnnId));
        var after = await PageAsync(client, "/guest/account");
        Assert.True(after.QuerySelector("#public-profile-on")!.HasAttribute("checked"));
        Assert.Equal("/residents/Ann/Sailor", after.QuerySelector("#public-profile-see")!.GetAttribute("href"));
        Assert.Equal("Saved.", after.QuerySelector("#change-public-profile .alert-success")!.TextContent.Trim());
        // At once, not after the minute the directory is kept.
        Assert.Equal(["Ann Sailor"], Names(await PageAsync(WorldMapApiTests.Client(f), "/residents")));

        using (var off = await SwitchAsync(client, false))
            Assert.Equal(HttpStatusCode.Redirect, off.StatusCode);
        Assert.Equal(PublicProfileState.Off, Choices(f).StateOf(AnnId));
        Assert.Empty(Names(await PageAsync(WorldMapApiTests.Client(f), "/residents")));
    }

    [Fact]
    public async Task A_held_resident_sees_why_and_cannot_turn_it_on()
    {
        await using var f = Factory();
        Choices(f).Hold(AnnId);
        var client = await AnnSignedInAsync(f);

        var page = await PageAsync(client, "/guest/account");
        Assert.NotNull(page.QuerySelector("#public-profile-held"));
        Assert.Null(page.QuerySelector("#change-public-profile"));

        using var r = await client.PostAsync("/guest/account?handler=PublicProfile", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["shown"] = "true", ["__RequestVerificationToken"] = Token(page, "form#change-language"),
        }), Ct);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(PublicProfileState.Held, Choices(f).StateOf(AnnId));
    }

    // ---- Staff ----

    private static async Task<HttpResponseMessage> StaffTaskAsync(HttpClient staff, Guid id, string task)
    {
        var page = await PageAsync(staff, $"/people/{id}");
        return await staff.PostAsync($"/people/{id}?handler=Task", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = id.ToString(), ["task"] = task, ["__RequestVerificationToken"] = Token(page, "#person-public-profile form"),
        }), Ct);
    }

    [Fact]
    public async Task A_supervisor_takes_a_resident_off_the_page_and_allows_them_again()
    {
        await using var f = Factory();
        Show(f, AnnId);
        var staff = await WorldMapApiTests.StaffAsync(f);

        var person = await PageAsync(staff, $"/people/{AnnId}");
        Assert.Contains("Residents page: shown", person.QuerySelector("#person-public-profile")!.TextContent);

        using (var hold = await StaffTaskAsync(staff, AnnId, "HoldProfile"))
            Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        Assert.Equal(PublicProfileState.Held, Choices(f).StateOf(AnnId));
        Assert.Empty(Names(await PageAsync(WorldMapApiTests.Client(f), "/residents")));
        var held = await PageAsync(staff, $"/people/{AnnId}");
        Assert.Contains("taken off by staff", held.QuerySelector("#person-public-profile")!.TextContent);
        Assert.Equal("ReleaseProfile", held.QuerySelector("#person-public-profile input[name=task]")!.GetAttribute("value"));

        using (var release = await StaffTaskAsync(staff, AnnId, "ReleaseProfile"))
            Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal(PublicProfileState.Off, Choices(f).StateOf(AnnId));
    }

    [Fact]
    public async Task Staff_may_not_hold_the_owner()
    {
        await using var f = Factory();
        var staff = await WorldMapApiTests.StaffAsync(f, "admin");
        var page = await PageAsync(staff, $"/people/{TestAuth.OwnerId}");
        Assert.NotNull(page.QuerySelector("#person-public-profile"));
        Assert.Null(page.QuerySelector("#person-public-profile form"));
    }
}
