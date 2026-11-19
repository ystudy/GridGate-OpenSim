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

    private static LauncherFactory Make(FakePairClient pair, OwnerPlan plan = OwnerPlan.RegionServer, ServerPairing? pairing = null,
        IReadOnlyList<RegionSettings>? regions = null)
    {
        var f = new LauncherFactory(s => s with
        {
            Regions = regions is null ? s.Regions : [.. regions],
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

    /// <summary>Signed in as <paramref name="role"/>; the owner has two-factor on unless <paramref name="twoFactor"/> is false.</summary>
    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string role = "owner", bool twoFactor = true)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        (await c.GetAsync("/__test/signin?role=" + role, Ct)).Dispose();
        if (role == "owner" && twoFactor) OwnerTwoFactorKit.Enrol(f);
        return OwnerTwoFactorKit.Track(c, f);
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
        // The Gloebit form asks for the owner's current two-factor code; a test that sets TwoFactorCode itself keeps its own.
        if (handler == "Gloebit" && !fields.ContainsKey("TwoFactorCode") && OwnerTwoFactorKit.CodeFor(c) is { } code)
            fields = new(fields) { ["TwoFactorCode"] = code };
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

        Assert.Contains("Paired.", MainText(doc));
        Assert.Equal([Code], pair.Codes);
        Assert.Equal("the-key", f.Grid.CurrentSettings().Money.Pairing?.Key);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-pair");
        Assert.Equal("ok", line.GetProperty("result").GetString());
        Assert.DoesNotContain(Code.Replace("-", ""), line.GetRawText().Replace("-", ""));
        Assert.DoesNotContain("the-key", line.GetRawText());
    }

    private static readonly Guid NearA = Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly Guid NearB = Guid.Parse("60000000-0000-0000-0000-000000000002");
    private static readonly Guid FarC = Guid.Parse("60000000-0000-0000-0000-000000000003");

    /// <summary>Two regions in program Near, one in program Far.</summary>
    private static IReadOnlyList<RegionSettings> ThreeRegions() =>
    [
        new RegionSettings("Near", "Near A", NearA, X: 1000, UdpPort: 9001),
        new RegionSettings("Near", "Near B", NearB, X: 1001, UdpPort: 9001),
        new RegionSettings("Far", "Far C", FarC, X: 1002, UdpPort: 9002),
    ];

    private static string[] Restarts(LauncherFactory f) =>
        f.Grid.Snapshot().Where(c => c.StartsWith("RegionPower Restart ", StringComparison.Ordinal)).ToArray();

    /// <summary>Waits until <paramref name="count"/> region restarts were asked for, then a moment more for any extra one.</summary>
    private static async Task<string[]> RestartsAsync(LauncherFactory f, int count)
    {
        for (var i = 0; i < 100 && Restarts(f).Length < count; i++) await Task.Delay(50, Ct);
        await Task.Delay(300, Ct);
        return Restarts(f);
    }

    [Fact]
    public async Task Pairing_restarts_each_running_region_program_once()
    {
        await using var f = Make(new FakePairClient(), regions: ThreeRegions());

        var doc = await PostAsync(await SignedInAsync(f), "Pair", new() { ["Code"] = Code });

        Assert.Contains("Paired. Restarting this server's regions so they start taking payments.", MainText(doc));
        var restarts = await RestartsAsync(f, 2);
        Assert.Equal(2, restarts.Length);
        Assert.Contains($"RegionPower Restart {FarC}", restarts);
        Assert.Single(restarts, c => c == $"RegionPower Restart {NearA}" || c == $"RegionPower Restart {NearB}");
    }

    [Fact]
    public async Task Pairing_leaves_a_stopped_program_stopped()
    {
        await using var f = Make(new FakePairClient(), regions: ThreeRegions());
        f.Grid.ProgramStates["Far"] = GridGate.Launcher.Core.Processes.ProcessState.Stopped;

        await PostAsync(await SignedInAsync(f), "Pair", new() { ["Code"] = Code });

        var only = Assert.Single(await RestartsAsync(f, 1));
        Assert.True(only == $"RegionPower Restart {NearA}" || only == $"RegionPower Restart {NearB}", only);
    }

    [Fact]
    public async Task Pairing_with_no_region_running_restarts_nothing_and_says_so()
    {
        await using var f = Make(new FakePairClient(), regions: ThreeRegions());
        f.Grid.ProgramStates["Near"] = GridGate.Launcher.Core.Processes.ProcessState.Stopped;
        f.Grid.ProgramStates["Far"] = GridGate.Launcher.Core.Processes.ProcessState.Stopped;

        var doc = await PostAsync(await SignedInAsync(f), "Pair", new() { ["Code"] = Code });

        Assert.Contains("Paired. This server's regions take payments once they start.", MainText(doc));
        Assert.Empty(await RestartsAsync(f, 0));
    }

    [Fact]
    public async Task A_refused_pairing_restarts_nothing()
    {
        var pair = new FakePairClient { Next = new(null, "The Grid Asset Server didn't accept this code.") };
        await using var f = Make(pair, regions: ThreeRegions());

        await PostAsync(await SignedInAsync(f), "Pair", new() { ["Code"] = Code });

        Assert.Empty(await RestartsAsync(f, 0));
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
    public async Task Unpair_restarts_each_running_region_program_once()
    {
        await using var f = Make(new FakePairClient(), pairing: new ServerPairing("192.168.1.5", new string('A', 64), "k", DateTimeOffset.UtcNow),
            regions: ThreeRegions());
        f.Grid.ProgramStates["Far"] = GridGate.Launcher.Core.Processes.ProcessState.Stopped;

        var doc = await PostAsync(await SignedInAsync(f), "Unpair", []);

        Assert.Contains("Unpaired. Restarting this server's regions so they stop using its key;", MainText(doc));
        Assert.Contains("remove it on the Grid Asset Server's Money page too", MainText(doc));
        var only = Assert.Single(await RestartsAsync(f, 1));
        Assert.True(only == $"RegionPower Restart {NearA}" || only == $"RegionPower Restart {NearB}", only);
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

    private static LauncherFactory GloebitGrid(GloebitSettings? app = null, IReadOnlyList<RegionSettings>? regions = null)
    {
        var f = Make(new FakePairClient(), regions: regions);
        f.Grid.UpdateSettings(s => s with { Join = s.Join! with { Money = MoneyMode.Gloebit }, Money = s.Money with { Gloebit = app } }, writeConfig: false);
        return f;
    }

    [Fact]
    public async Task Gloebit_grid_shows_the_app_form_not_pairing()
    {
        await using var f = GloebitGrid();

        var doc = await PageAsync(await SignedInAsync(f), "/advanced/money");

        Assert.Contains("The grid uses Gloebit.", MainText(doc));
        Assert.DoesNotContain("paired", doc.QuerySelector("h1")!.TextContent);
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
        Assert.Contains("Saved.", MainText(doc));
        var html = (await PageAsync(c, "/advanced/money")).DocumentElement.OuterHtml;
        Assert.DoesNotContain(GApp.Secret, html);
        Assert.DoesNotContain(GApp.Key, html);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-gloebit");
        Assert.DoesNotContain(GApp.Secret, line.GetRawText());
    }

    [Fact]
    public async Task Saving_the_gloebit_app_restarts_each_running_region_program_once()
    {
        await using var f = GloebitGrid(regions: ThreeRegions());

        var doc = await PostAsync(await SignedInAsync(f), "Gloebit", new()
        {
            ["Environment"] = "sandbox", ["Key"] = GApp.Key, ["Secret"] = GApp.Secret, ["OwnerName"] = "RS Owner", ["OwnerEmail"] = "",
        });

        Assert.Contains("Saved. Restarting this server's regions so they start Gloebit.", MainText(doc));
        var restarts = await RestartsAsync(f, 2);
        Assert.Equal(2, restarts.Length);
        Assert.Contains($"RegionPower Restart {FarC}", restarts);
    }

    [Fact]
    public async Task Saving_the_gloebit_app_with_no_region_running_says_when_it_starts()
    {
        await using var f = GloebitGrid(regions: ThreeRegions());
        f.Grid.ProgramStates["Near"] = GridGate.Launcher.Core.Processes.ProcessState.Stopped;
        f.Grid.ProgramStates["Far"] = GridGate.Launcher.Core.Processes.ProcessState.Stopped;

        var doc = await PostAsync(await SignedInAsync(f), "Gloebit", new()
        {
            ["Environment"] = "sandbox", ["Key"] = GApp.Key, ["Secret"] = GApp.Secret, ["OwnerName"] = "RS Owner", ["OwnerEmail"] = "",
        });

        Assert.Contains("Saved. This server's regions start Gloebit when they start.", MainText(doc));
        Assert.Empty(await RestartsAsync(f, 0));
    }

    [Fact]
    public async Task A_refused_gloebit_app_restarts_nothing()
    {
        await using var f = GloebitGrid(regions: ThreeRegions());

        var doc = await PostAsync(await SignedInAsync(f), "Gloebit", new()
        {
            ["Environment"] = "sandbox", ["Key"] = "", ["Secret"] = "", ["OwnerName"] = "RS Owner", ["OwnerEmail"] = "",
        });

        Assert.NotNull(doc.QuerySelector(".alert-danger"));
        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
        Assert.Empty(await RestartsAsync(f, 0));
    }

    // ---- the owner's two-factor code for the Gloebit app ----

    private static Dictionary<string, string> AppFields(string? code = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Environment"] = "sandbox", ["Key"] = GApp.Key, ["Secret"] = GApp.Secret, ["OwnerName"] = "RS Owner", ["OwnerEmail"] = "",
        };
        if (code is not null) fields["TwoFactorCode"] = code;
        return fields;
    }

    [Theory]
    [InlineData("")]
    [InlineData("000000")]
    public async Task Saving_the_gloebit_app_needs_a_right_two_factor_code(string code)
    {
        await using var f = GloebitGrid(regions: ThreeRegions());
        var c = await SignedInAsync(f);

        var doc = await PostAsync(c, "Gloebit", AppFields(code));

        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
        Assert.NotNull(doc.QuerySelector(".alert-danger"));
        Assert.Empty(await RestartsAsync(f, 0));
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-gloebit");
        Assert.StartsWith("refused", line.GetProperty("result").GetString());
        Assert.DoesNotContain(GApp.Secret, line.GetRawText());
        Assert.DoesNotContain(GApp.Secret, doc.DocumentElement.OuterHtml);
    }

    [Fact]
    public async Task Five_wrong_two_factor_codes_lock_the_owner_out_even_with_the_right_one()
    {
        await using var f = GloebitGrid(regions: ThreeRegions());
        var c = await SignedInAsync(f);
        for (var i = 0; i < 5; i++) await PostAsync(c, "Gloebit", AppFields("000000"));

        var doc = await PostAsync(c, "Gloebit", AppFields(OwnerTwoFactorKit.NextCode(f)));

        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
        Assert.Contains("Too many attempts", MainText(doc));
    }

    [Fact]
    public async Task A_two_factor_code_that_was_used_does_not_save_a_second_time()
    {
        await using var f = GloebitGrid(regions: ThreeRegions());
        var c = await SignedInAsync(f);
        var code = OwnerTwoFactorKit.NextCode(f);
        await PostAsync(c, "Gloebit", AppFields(code));
        Assert.Equal(GApp, f.Grid.CurrentSettings().Money.Gloebit);
        f.Grid.UpdateSettings(s => s with { Money = s.Money with { Gloebit = null } }, writeConfig: false);

        await PostAsync(c, "Gloebit", AppFields(code));

        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
    }

    [Fact]
    public async Task With_an_unreadable_two_factor_file_the_page_says_it_cant_be_checked_and_nothing_saves()
    {
        await using var f = GloebitGrid(regions: ThreeRegions());
        var c = await SignedInAsync(f, twoFactor: false);
        Directory.CreateDirectory(f.Paths.Security);
        File.WriteAllText(Path.Combine(f.Paths.Security, "twofactor.json"), "{ this is not json");

        var page = await PageAsync(c, "/advanced/money");
        var doc = await PostAsync(c, "Gloebit", AppFields("123456"));

        Assert.Contains("can't be checked right now", page.QuerySelector("#rs-gloebit-two-factor")!.TextContent);
        Assert.DoesNotContain("isn't on", page.QuerySelector("#rs-gloebit-two-factor")!.TextContent);
        Assert.Contains("can't be checked right now", doc.QuerySelector(".alert-danger")!.TextContent);
        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-gloebit");
        Assert.Equal("refused: two-factor can't be checked", line.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Without_two_factor_the_owner_is_told_where_to_turn_it_on_and_nothing_saves()
    {
        await using var f = GloebitGrid(regions: ThreeRegions());
        var c = await SignedInAsync(f, twoFactor: false);

        var page = await PageAsync(c, "/advanced/money");
        var doc = await PostAsync(c, "Gloebit", AppFields("123456"));

        Assert.Null(page.QuerySelector("form[action*='handler=Gloebit'] button[type=submit]"));
        Assert.Null(page.QuerySelector("input[name=TwoFactorCode]"));
        Assert.NotNull(page.QuerySelector("a[href='/profile/two-factor']"));
        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
        Assert.NotNull(doc.QuerySelector(".alert-danger"));
        Assert.Empty(await RestartsAsync(f, 0));
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-gloebit");
        Assert.StartsWith("refused", line.GetProperty("result").GetString());
    }

    [Fact]
    public async Task The_gloebit_form_asks_for_the_code_and_never_shows_one_back()
    {
        await using var f = GloebitGrid();
        var c = await SignedInAsync(f);

        var page = await PageAsync(c, "/advanced/money");
        var input = page.QuerySelector("form[action*='handler=Gloebit'] input[name=TwoFactorCode]");
        var after = await PostAsync(c, "Gloebit", AppFields("654321"));

        Assert.NotNull(input);
        Assert.Equal("one-time-code", input!.GetAttribute("autocomplete"));
        Assert.DoesNotContain("654321", after.DocumentElement.OuterHtml);
    }

    [Fact]
    public async Task The_dashboard_shows_the_masked_key_and_time_when_this_servers_gloebit_app_changes()
    {
        await using var f = GloebitGrid();
        var c = await SignedInAsync(f);
        var before = await PageAsync(c, "/");
        Assert.Null(before.QuerySelector("#gloebit-app-changed"));
        await PostAsync(c, "Gloebit", AppFields());
        var at = f.Time.GetUtcNow();

        var doc = await PageAsync(c, "/");

        var text = doc.QuerySelector("#gloebit-app-changed")!.TextContent;
        Assert.Contains(GloebitRules.Masked(GApp.Key), text);
        Assert.Contains(at.ToString("yyyy-MM-dd HH:mm"), text);
        Assert.DoesNotContain(GApp.Key, doc.DocumentElement.OuterHtml);
        Assert.DoesNotContain(GApp.Secret, doc.DocumentElement.OuterHtml);
    }

    [Fact]
    public async Task Admin_cannot_save_this_servers_gloebit_app()
    {
        // Gloebit is real dollars: only this computer's owner sets its app (money design §9.1).
        await using var f = GloebitGrid(regions: ThreeRegions());
        var c = await SignedInAsync(f, "admin");
        var page = await PageAsync(c, "/advanced/money");
        var token = page.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var r = await c.PostAsync("/advanced/money?handler=Gloebit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Environment"] = "sandbox", ["Key"] = GApp.Key, ["Secret"] = GApp.Secret, ["OwnerName"] = "RS Owner", ["OwnerEmail"] = "",
            ["__RequestVerificationToken"] = token,
        }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
        Assert.Empty(await RestartsAsync(f, 0));
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-gloebit");
        Assert.Equal("refused: the owner's", line.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Admin_sees_no_gloebit_form()
    {
        await using var f = GloebitGrid();

        var doc = await PageAsync(await SignedInAsync(f, "admin"), "/advanced/money");

        Assert.Null(doc.QuerySelector("form[action*='handler=Gloebit']"));
        Assert.Contains("Only this computer's owner can save its Gloebit app.", MainText(doc));
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
