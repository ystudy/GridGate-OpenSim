using System.Net;
using System.Text.Json;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Settings;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The world map's data (spec 2026-11-20-public-map-design, "The data links"): only for a signed-in resident or staff member,
/// only the grid's own regions with their sizes and people, kept 30 seconds and shared, and nothing without a guest side.
/// </summary>
public sealed class WorldMapApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid WelcomeId = Guid.Parse("70000000-0000-0000-0000-000000000001");
    private static readonly Guid SandboxId = Guid.Parse("70000000-0000-0000-0000-000000000002");
    private static readonly Guid OtherId = Guid.Parse("70000000-0000-0000-0000-000000000003");

    /// <summary>Avalon, set up, with Sandbox, a 2×2 Welcome holding two people, and a hypergrid link; <paramref name="outside"/> puts every caller at 192.168.1.50.</summary>
    internal static LauncherFactory Factory(Func<LauncherSettings, LauncherSettings>? settings = null, bool outside = false)
    {
        var f = new LauncherFactory(s => (settings ?? (x => x))(s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Setup = s.Setup with { OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
        }))
        {
            Before = app =>
            {
                if (outside)
                    app.Use((ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
                        return next(ctx);
                    });
                TestAuth.Apply(app);
            },
        };
        TestAuth.AddStaff(f.Robust);
        f.Regions.Result = new GridRegionList(
        [
            new GridRegion(SandboxId, "Sandbox", 1002, 1000),
            new GridRegion(WelcomeId, "Welcome", 1000, 1000, SizeX: 2, SizeY: 2),
            new GridRegion(OtherId, "Other", 2000, 2000, IsLink: true),
        ], true);
        f.OnlineUsers.Result = new OnlineList([new OnlineAvatar("Ann A", WelcomeId), new OnlineAvatar("Bob B", WelcomeId)], true);
        return f;
    }

    internal static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false });

    internal static async Task<HttpClient> ResidentAsync(LauncherFactory f)
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/guest-signin", Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    internal static async Task<HttpClient> StaffAsync(LauncherFactory f, string role = "supervisor")
    {
        var client = Client(f);
        using var r = await client.GetAsync("/__test/signin?role=" + role, Ct);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return client;
    }

    internal static LegalSettings Terms(int version) => new(new Dictionary<string, IReadOnlyDictionary<string, LegalText>>
    {
        ["en"] = new Dictionary<string, LegalText> { [LegalPages.Terms] = new("Test terms one.", version, DateTimeOffset.UtcNow) },
    });

    private static async Task<JsonElement> RegionsAsync(HttpClient client)
    {
        using var r = await client.GetAsync("/world/regions", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static List<string> Names(JsonElement json) =>
        [.. json.GetProperty("regions").EnumerateArray().Select(r => r.GetProperty("name").GetString()!)];

    [Fact]
    public async Task Regions_need_a_sign_in()
    {
        await using var f = Factory();

        foreach (var url in new[] { "/world/regions", "/world/tile/1/1000/1000" })
        {
            using var r = await Client(f).GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Null(r.Headers.Location);
        }
    }

    [Fact]
    public async Task Regions_list_own_regions_with_sizes_and_people()
    {
        await using var f = Factory();

        var json = await RegionsAsync(await ResidentAsync(f));

        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.Equal(["Sandbox", "Welcome"], Names(json));
        var sandbox = json.GetProperty("regions")[0];
        Assert.Equal(1002, sandbox.GetProperty("x").GetInt32());
        Assert.Equal(1000, sandbox.GetProperty("y").GetInt32());
        Assert.Equal(1, sandbox.GetProperty("w").GetInt32());
        Assert.Equal(1, sandbox.GetProperty("h").GetInt32());
        Assert.Equal(0, sandbox.GetProperty("people").GetInt32());
        var welcome = json.GetProperty("regions")[1];
        Assert.Equal(2, welcome.GetProperty("w").GetInt32());
        Assert.Equal(2, welcome.GetProperty("h").GetInt32());
        Assert.Equal(2, welcome.GetProperty("people").GetInt32());
        var text = json.GetRawText();
        Assert.DoesNotContain("\"id\"", text);
        Assert.DoesNotContain("\"state\"", text);
        Assert.DoesNotContain(WelcomeId.ToString(), text);
    }

    [Fact]
    public async Task Staff_can_read_the_regions()
    {
        await using var f = Factory();

        Assert.Equal(["Sandbox", "Welcome"], Names(await RegionsAsync(await StaffAsync(f))));
    }

    [Fact]
    public async Task People_unknown_when_the_online_list_is_down()
    {
        await using var f = Factory();
        f.OnlineUsers.Result = OnlineList.Unavailable;

        var json = await RegionsAsync(await ResidentAsync(f));

        Assert.All(json.GetProperty("regions").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("people").ValueKind));
    }

    [Fact]
    public async Task Regions_unavailable_says_so()
    {
        await using var f = Factory();
        f.Regions.Result = GridRegionList.Unavailable;

        var json = await RegionsAsync(await ResidentAsync(f));

        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.Empty(Names(json));
    }

    [Fact]
    public async Task The_list_is_kept_30_seconds()
    {
        await using var f = Factory();
        var client = await ResidentAsync(f);
        Assert.Equal(2, Names(await RegionsAsync(client)).Count);

        f.Regions.Result = new GridRegionList([new GridRegion(SandboxId, "Sandbox", 1002, 1000)], true);
        Assert.Equal(2, Names(await RegionsAsync(client)).Count);

        f.Time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(["Sandbox"], Names(await RegionsAsync(client)));
    }

    [Fact]
    public async Task Teleport_starts_at_the_login_address()
    {
        await using var f = Factory(s => s with { Hostname = "grid.example.com", RobustPublicPort = 8002 });
        var client = await ResidentAsync(f);

        Assert.Equal("hop://grid.example.com:8002/", (await RegionsAsync(client)).GetProperty("teleport").GetString());

        f.Store.Save(f.Store.LoadOrCreate() with { RobustPublicPort = 9000 });
        Assert.Equal("hop://grid.example.com:9000/", (await RegionsAsync(client)).GetProperty("teleport").GetString());
    }

    [Theory]
    [InlineData("/world/tile/0/0/0")]
    [InlineData("/world/tile/9/0/0")]
    [InlineData("/world/tile/2/1/0")]
    public async Task World_tiles_check_their_numbers(string url)
    {
        await using var f = Factory();

        using var r = await (await ResidentAsync(f)).GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Not_there_without_a_guest_side()
    {
        await using var f = Factory(s => s with { Plan = OwnerPlan.RegionServer });
        var staff = await StaffAsync(f, "owner");

        foreach (var url in new[] { "/world/regions", "/world/tile/1/1000/1000" })
        {
            using var r = await staff.GetAsync(url, Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
    }

    [Fact]
    public async Task A_resident_who_owes_the_Terms_gets_401()
    {
        await using var f = Factory(s => s with { Legal = Terms(1) });

        using var r = await (await ResidentAsync(f)).GetAsync("/world/regions", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Regions_reachable_outside_the_staff_networks()
    {
        await using var f = Factory(outside: true);
        f.Store.Save(f.Store.LoadOrCreate() with { Security = f.Store.LoadOrCreate().Security with { AdminNetworks = ["10.0.0.0/8"] } });

        Assert.Equal(["Sandbox", "Welcome"], Names(await RegionsAsync(await ResidentAsync(f))));
    }
}
