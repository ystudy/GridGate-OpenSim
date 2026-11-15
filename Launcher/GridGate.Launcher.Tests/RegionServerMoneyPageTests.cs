using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The pairing client, faked for the Region Server's Money page.</summary>
internal sealed class FakePairClient : IMoneyPairClient
{
    public PairOutcome Next { get; set; } = new(new ServerPairing("192.168.1.5", new string('A', 64), "the-key", DateTimeOffset.UtcNow), null);
    /// <summary>Runs while the pairing is "in flight", before it answers.</summary>
    public Action? During { get; set; }
    public PairingHealth Health { get; set; } = PairingHealth.Ok;
    public List<string> Codes { get; } = [];

    public Task<PairOutcome> PairAsync(LauncherSettings s, string typedCode, CancellationToken ct)
    {
        Codes.Add(typedCode);
        During?.Invoke();
        return Task.FromResult(Next);
    }

    public Task<PairingHealth> CheckAsync(LauncherSettings s, CancellationToken ct) => Task.FromResult(Health);
}

/// <summary>A Region Server's Money page (<c>/advanced/money</c>; money plan 3, task 6).</summary>
public sealed class RegionServerMoneyPageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();
    private static readonly Guid OwnerId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private const string Code = "7K2M-9QXD-4HTB-ZW3R";

    private static JoinedGrid Avalon(string host = "192.168.1.5") =>
        new(host, 8002, 8003, "Avalon", $"http://{host}:8002/", LauncherVersion.Current, false, 8004, 8006,
            MoneyMode.Play, 8000, new string('A', 64), $"http://{host}:8001/economy/");

    private static LauncherFactory Make(FakePairClient pair, OwnerPlan plan = OwnerPlan.RegionServer, ServerPairing? pairing = null)
    {
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            GridName = "Avalon",
            Plan = plan,
            Join = plan == OwnerPlan.RegionServer ? Avalon() : null,
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true, Owner: new OwnerInfo("Estate", "Owner", OwnerId)),
            Money = new MoneySettings(Pairing: pairing),
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services =>
            {
                services.RemoveAll<IMoneyPairClient>();
                services.AddSingleton<IMoneyPairClient>(pair);
                services.RemoveAll<MoneyGate>();
                services.AddSingleton(sp => new MoneyGate(sp.GetRequiredService<Func<LauncherSettings>>(), MoneyGate.Open));
            },
        };
        TestAuth.AddStaff(f.Robust);
        return f;
    }

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner")
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        (await c.GetAsync("/__test/signin?role=" + role, Ct)).Dispose();
        return c;
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient c, string url)
    {
        using var r = await c.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<IHtmlDocument> PostAsync(HttpClient c, string handler, Dictionary<string, string> fields)
    {
        var doc = await PageAsync(c, "/advanced/money");
        var token = doc.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        using var r = await c.PostAsync($"/advanced/money?handler={handler}",
            new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
    }

    private static string MainText(IHtmlDocument d) => d.QuerySelector("main")!.TextContent;

    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.GridAsset)]
    [InlineData(OwnerPlan.Connected)]
    public async Task Page_is_404_off_region_server(OwnerPlan plan)
    {
        await using var f = Make(new FakePairClient(), plan);

        using var r = await (await SignedInAsync(f)).GetAsync("/advanced/money", Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Page_shows_the_grids_money_and_no_pairing()
    {
        await using var f = Make(new FakePairClient());

        var text = MainText(await PageAsync(await SignedInAsync(f), "/advanced/money"));

        Assert.Contains("The grid's money is Play coins.", text);
        Assert.Contains("Not paired: these regions refuse payments.", text);
    }

    [Fact]
    public async Task Pairing_saves_and_never_audits_the_code()
    {
        var pair = new FakePairClient();
        await using var f = Make(pair);

        var doc = await PostAsync(await SignedInAsync(f), "Pair", new() { ["Code"] = Code });

        Assert.Contains("Paired. Restart the regions to start taking payments.", MainText(doc));
        Assert.Equal([Code], pair.Codes);
        Assert.Equal("the-key", f.Grid.CurrentSettings().Money.Pairing?.Key);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-pair");
        Assert.Equal("ok", line.GetProperty("result").GetString());
        Assert.DoesNotContain(Code.Replace("-", ""), line.GetRawText().Replace("-", ""));
        Assert.DoesNotContain("the-key", line.GetRawText());
    }

    [Fact]
    public async Task Refused_code_saves_nothing()
    {
        var pair = new FakePairClient { Next = new(null, "The Grid Asset Server didn't accept this code.") };
        await using var f = Make(pair);

        var doc = await PostAsync(await SignedInAsync(f), "Pair", new() { ["Code"] = Code });

        Assert.Contains("The Grid Asset Server didn't accept this code.", doc.QuerySelector(".alert-danger")!.TextContent);
        Assert.Null(f.Grid.CurrentSettings().Money.Pairing);
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-pair" && a.GetProperty("result").GetString() == "refused");
    }

    [Fact]
    public async Task Paired_page_says_when()
    {
        await using var f = Make(new FakePairClient(), pairing: new ServerPairing("192.168.1.5", new string('A', 64), "k", new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero)));

        Assert.Contains("Paired on 2026-11-14.", MainText(await PageAsync(await SignedInAsync(f), "/advanced/money")));
    }

    [Fact]
    public async Task Changed_certificate_asks_to_pair_again()
    {
        await using var f = Make(new FakePairClient(), pairing: new ServerPairing("192.168.1.5", new string('0', 64), "k", DateTimeOffset.UtcNow));

        Assert.Contains("The grid's money certificate changed: pair again.", MainText(await PageAsync(await SignedInAsync(f), "/advanced/money")));
    }

    [Fact]
    public async Task Check_says_what_the_grid_answered()
    {
        var pair = new FakePairClient { Health = PairingHealth.Refused };
        await using var f = Make(pair, pairing: new ServerPairing("192.168.1.5", new string('A', 64), "k", DateTimeOffset.UtcNow));

        var doc = await PostAsync(await SignedInAsync(f), "Check", []);

        Assert.Contains("The grid no longer accepts this server's key. Pair again.", MainText(doc));
    }

    [Fact]
    public async Task Unpair_clears_the_pairing()
    {
        await using var f = Make(new FakePairClient(), pairing: new ServerPairing("192.168.1.5", new string('A', 64), "k", DateTimeOffset.UtcNow));

        var doc = await PostAsync(await SignedInAsync(f), "Unpair", []);

        Assert.Null(f.Grid.CurrentSettings().Money.Pairing);
        Assert.Contains("remove it on the Grid Asset Server's Money page too", MainText(doc));
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-unpair");
    }

    [Fact]
    public async Task Stale_join_still_offers_pairing_and_saves_the_fresh_grid_info()
    {
        var fresh = Avalon() with { MoneyFingerprint = new string('B', 64) };
        var pair = new FakePairClient
        {
            Next = new(new ServerPairing("192.168.1.5", new string('B', 64), "the-key", DateTimeOffset.UtcNow), null, fresh),
        };
        await using var f = Make(pair);
        f.Grid.UpdateSettings(s => s with { Join = s.Join! with { Money = MoneyMode.Off, MoneyFingerprint = null } }, writeConfig: false);
        var c = await SignedInAsync(f);

        Assert.NotNull((await PageAsync(c, "/advanced/money")).QuerySelector("form[action*='handler=Pair']"));
        await PostAsync(c, "Pair", new() { ["Code"] = Code });

        var join = f.Grid.CurrentSettings().Join!;
        Assert.Equal(MoneyMode.Play, join.Money);
        Assert.Equal(new string('B', 64), join.MoneyFingerprint);
        Assert.Equal("192.168.1.5", join.Host);
    }

    [Fact]
    public async Task Re_point_during_pairing_saves_neither()
    {
        var pair = new FakePairClient { Next = new(new ServerPairing("192.168.1.5", new string('B', 64), "the-key", DateTimeOffset.UtcNow), null, Avalon()) };
        await using var f = Make(pair);
        var elsewhere = Avalon("192.168.1.9");
        pair.During = () => f.Grid.UpdateSettings(s => s with { Join = elsewhere }, writeConfig: false);

        var doc = await PostAsync(await SignedInAsync(f), "Pair", new() { ["Code"] = Code });

        Assert.Null(f.Grid.CurrentSettings().Money.Pairing);
        Assert.Equal(elsewhere, f.Grid.CurrentSettings().Join);
        Assert.Contains("The Grid Asset Server changed while pairing. Pair again.", doc.QuerySelector(".alert-danger")!.TextContent);
    }

    [Fact]
    public async Task Partial_coverage_is_said()
    {
        var pair = new FakePairClient { Health = PairingHealth.SomeRegions };
        await using var f = Make(pair, pairing: new ServerPairing("192.168.1.5", new string('A', 64), "k", DateTimeOffset.UtcNow));

        var doc = await PostAsync(await SignedInAsync(f), "Check", []);

        Assert.Contains("The grid sees only some of this server's regions.", MainText(doc));
    }

    [Fact]
    public async Task No_regions_seen_is_said()
    {
        var pair = new FakePairClient { Health = PairingHealth.NoRegions };
        await using var f = Make(pair, pairing: new ServerPairing("192.168.1.5", new string('A', 64), "k", DateTimeOffset.UtcNow));

        var doc = await PostAsync(await SignedInAsync(f), "Check", []);

        Assert.Contains("The grid accepts this server's key, but sees none of its regions yet.", MainText(doc));
    }

    // ---- Gloebit on a Region Server (money plan 4, task 5) ----

    private static readonly GloebitSettings GApp = new("sandbox", "0b9e3c2a-1111-4c5d-8e9f-0123456789ab", "7d3f9a10-2222-4b6c-9d8e-fedcba987654", "RS Owner");

    private static LauncherFactory GloebitGrid(GloebitSettings? app = null)
    {
        var f = Make(new FakePairClient());
        f.Grid.UpdateSettings(s => s with { Join = s.Join! with { Money = MoneyMode.Gloebit }, Money = s.Money with { Gloebit = app } }, writeConfig: false);
        return f;
    }

    [Fact]
    public async Task Gloebit_grid_shows_the_app_form_not_pairing()
    {
        await using var f = GloebitGrid();

        var doc = await PageAsync(await SignedInAsync(f), "/advanced/money");

        Assert.Contains("The grid uses Gloebit.", MainText(doc));
        Assert.NotNull(doc.QuerySelector("form[action*='handler=Gloebit']"));
        Assert.Null(doc.QuerySelector("form[action*='handler=Pair']"));
        Assert.Contains("These regions refuse payments until this computer's Gloebit app is saved.", MainText(doc));
    }

    [Fact]
    public async Task Region_server_saves_its_own_gloebit_app()
    {
        await using var f = GloebitGrid();
        var c = await SignedInAsync(f);

        var doc = await PostAsync(c, "Gloebit", new()
        {
            ["Environment"] = "sandbox", ["Key"] = GApp.Key, ["Secret"] = GApp.Secret, ["OwnerName"] = "RS Owner", ["OwnerEmail"] = "",
        });

        Assert.Equal(GApp, f.Grid.CurrentSettings().Money.Gloebit);
        Assert.Contains("Saved. Restart the regions to start Gloebit.", MainText(doc));
        var html = (await PageAsync(c, "/advanced/money")).DocumentElement.OuterHtml;
        Assert.DoesNotContain(GApp.Secret, html);
        Assert.DoesNotContain(GApp.Key, html);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-gloebit");
        Assert.DoesNotContain(GApp.Secret, line.GetRawText());
    }

    [Fact]
    public async Task Coins_grid_has_no_gloebit_form()
    {
        await using var f = Make(new FakePairClient());

        Assert.Null((await PageAsync(await SignedInAsync(f), "/advanced/money")).QuerySelector("form[action*='handler=Gloebit']"));
    }

    [Fact]
    public async Task Closed_gate_offers_no_pairing()
    {
        var pair = new FakePairClient();
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Plan = OwnerPlan.RegionServer,
            Join = Avalon(),
            Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true, Owner: new OwnerInfo("Estate", "Owner", OwnerId)),
        })
        { Before = TestAuth.Apply };
        await using (f)
        {
            TestAuth.AddStaff(f.Robust);
            var doc = await PageAsync(await SignedInAsync(f), "/advanced/money");
            Assert.Contains("The grid's money is off.", MainText(doc));
            Assert.Null(doc.QuerySelector("form[action*='handler=Pair']"));
        }
    }
}
