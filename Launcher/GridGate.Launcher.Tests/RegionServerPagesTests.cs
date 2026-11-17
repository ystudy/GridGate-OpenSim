using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Processes;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Task 6 of plan 6b: who may sign in to a Region Server, how the dashboard shows the Grid Asset Server, and Advanced settings.</summary>
public sealed class RegionServerPagesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid OwnerId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    private static JoinedGrid Avalon(string name = "Avalon") =>
        new("192.168.1.5", 8002, 8003, name, "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006);

    private static LauncherSettings Settings(OwnerPlan plan, Guid? owner = null) => TestSettings.Create() with
    {
        Plan = plan,
        Join = plan == OwnerPlan.RegionServer ? Avalon() : null,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
            Owner: new OwnerInfo("Estate", "Owner", owner ?? OwnerId)),
    };

    private static RobustAccount Account(int level, Guid? id = null) => new(id ?? Guid.NewGuid(), "A", "B", level, "");

    // ---- AdminRule ----

    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.GridAsset)]
    [InlineData(OwnerPlan.RegionServer)]
    public void Level_200_may_administer_on_any_plan(OwnerPlan plan) =>
        Assert.True(AdminRule.MayAdminister(Settings(plan), Account(200)));

    [Fact]
    public void Level_0_estate_owner_may_administer_on_a_region_server() =>
        Assert.True(AdminRule.MayAdminister(Settings(OwnerPlan.RegionServer), Account(0, OwnerId)));

    [Fact]
    public void Level_0_owner_may_administer_on_local() =>
        Assert.True(AdminRule.MayAdminister(Settings(OwnerPlan.Local), Account(0, OwnerId)));

    [Fact]
    public void Level_0_non_owner_may_not_administer_on_a_region_server() =>
        Assert.False(AdminRule.MayAdminister(Settings(OwnerPlan.RegionServer), Account(0)));

    [Fact]
    public void Nobody_below_200_may_administer_when_no_owner_is_named() =>
        Assert.False(AdminRule.MayAdminister(Settings(OwnerPlan.RegionServer) with { Setup = new SetupState() }, Account(0, OwnerId)));

    // ---- helpers ----

    private static LauncherFactory Factory(OwnerPlan plan, Func<LauncherSettings, LauncherSettings>? more = null) => new(s => (more ?? (x => x))(s with
    {
        SetupToken = null,
        GridName = "Avalon",
        Plan = plan,
        Join = plan == OwnerPlan.RegionServer ? Avalon() : null,
        Hypergrid = false,
        Setup = new SetupState(WorldNamed: true, OwnerCreated: true, RegionCreated: true, Finished: true,
            Owner: new OwnerInfo("Estate", "Owner", OwnerId)),
    }))
    {
        Before = TestAuth.Apply,
    };

    private static async Task<HttpClient> SignedInAsync(LauncherFactory f, string? role = null)
    {
        var client = f.CreateClient(new() { AllowAutoRedirect = false });
        (await client.GetAsync(role is null ? "/__test/signin" : "/__test/signin?role=" + role, Ct)).Dispose();
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields, string? tokenFrom = null)
    {
        var page = await client.GetStringAsync(tokenFrom ?? url.Split('?')[0], Ct);
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token }), Ct);
    }

    private static string MainText(string html) => new HtmlParser().ParseDocument(html).QuerySelector("main")!.TextContent;

    // ---- sign-in ----

    [Fact]
    public async Task Level_0_estate_owner_signs_in_to_a_region_server()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Robust.Add("Estate", "Owner", 0, "pass-word1", OwnerId).Add("Plain", "Person", 0, "pass-word1");
        var client = f.CreateClient(new() { AllowAutoRedirect = false });

        using var ok = await PostAsync(client, "/login", new() { ["Name"] = "Estate Owner", ["Password"] = "pass-word1" });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        var other = f.CreateClient(new() { AllowAutoRedirect = false });
        using var refused = await PostAsync(other, "/login", new() { ["Name"] = "Plain Person", ["Password"] = "pass-word1" });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
    }

    [Fact]
    public async Task Level_0_owner_signs_in_on_a_local_grid()
    {
        await using var f = Factory(OwnerPlan.Local);
        f.Robust.Add("Estate", "Owner", 0, "pass-word1", OwnerId);
        var client = f.CreateClient(new() { AllowAutoRedirect = false });

        using var signedIn = await PostAsync(client, "/login", new() { ["Name"] = "Estate Owner", ["Password"] = "pass-word1" });
        Assert.Equal(HttpStatusCode.Found, signedIn.StatusCode);
    }

    // ---- status and dashboard ----

    private static GridStatus RegionServerStatus(ProcessState gridAsset)
    {
        ComponentStatus C(string n, ProcessState s) => new(n, s, null, null, null, null);
        return new GridStatus(C("database", ProcessState.Running), C("robust", gridAsset), [], [], false, "http://192.168.1.5:8002/",
            RunsRobust: false, OtherGridAddress: "192.168.1.5:8002");
    }

    [Fact]
    public async Task Status_json_labels_the_grid_asset_server_with_its_address()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Grid.Status = RegionServerStatus(ProcessState.Running);
        using var client = f.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));
        var robust = doc.RootElement.GetProperty("grid").GetProperty("robust");
        Assert.Equal("Grid Asset Server", robust.GetProperty("label").GetString());
        Assert.Equal("192.168.1.5:8002", robust.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Status_problem_names_the_grid_asset_server_when_it_does_not_answer()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Grid.Status = RegionServerStatus(ProcessState.Failed);
        using var client = f.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/status", Ct));
        Assert.Equal("The Grid Asset Server isn't answering.", doc.RootElement.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Region_server_dashboard_has_no_restart_grid_service_button()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Grid.Status = RegionServerStatus(ProcessState.Running);
        using var client = await SignedInAsync(f);

        var html = await client.GetStringAsync("/", Ct);
        Assert.DoesNotContain("restart-robust", html);
        Assert.DoesNotContain("Restart the grid service", html);
        Assert.Contains("Grid Asset Server", html);
        Assert.Contains("restart-database", html);
    }

    [Theory]
    [InlineData(OwnerPlan.RegionServer, "Restarting the database. Your regions restart with it.")]
    [InlineData(OwnerPlan.Local, "Restarting the database. The grid service and your regions restart with it.")]
    public async Task Restart_database_notice_names_the_grid_service_only_when_this_computer_runs_one(OwnerPlan plan, string notice)
    {
        await using var f = Factory(plan);
        using var client = await SignedInAsync(f);
        var token = new HtmlParser().ParseDocument(await client.GetStringAsync("/", Ct))
            .QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/grid/restart-database");
        request.Headers.Add("RequestVerificationToken", token);

        using var resp = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal(notice, json.RootElement.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task Local_dashboard_keeps_the_restart_grid_service_button_and_label()
    {
        await using var f = Factory(OwnerPlan.Local);
        using var client = await SignedInAsync(f);

        var html = await client.GetStringAsync("/", Ct);
        Assert.Contains("restart-robust", html);
        Assert.Contains("Grid service", html);
    }

    [Fact]
    public async Task Coming_soon_does_not_name_the_region_server()
    {
        await using var f = Factory(OwnerPlan.Local);
        using var client = await SignedInAsync(f);

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/", Ct));
        Assert.DoesNotContain(doc.QuerySelectorAll(".coming-soon li"), li => li.TextContent.Contains("Region Server"));
    }

    // ---- Advanced: plan ----

    [Fact]
    public async Task Leaving_a_grid_asset_server_is_refused_while_other_computers_have_regions_joined()
    {
        await using var f = Factory(OwnerPlan.GridAsset);
        f.Grid.JoinedRegions = 2;
        using var client = await SignedInAsync(f);

        using var resp = await PostAsync(client, "/advanced/plan?handler=Change", new() { ["To"] = "local" });
        var text = MainText(await resp.Content.ReadAsStringAsync(Ct));

        Assert.Contains("2 region(s) from other computers are joined to this grid. Move them to another grid or shut those computers down first.", text);
        Assert.Equal(OwnerPlan.GridAsset, f.Store.LoadOrCreate().Plan);
    }

    [Fact]
    public async Task Leaving_a_grid_asset_server_is_refused_when_the_grid_service_does_not_answer()
    {
        await using var f = Factory(OwnerPlan.GridAsset);
        f.Grid.JoinedRegionError = new InvalidOperationException("down");
        using var client = await SignedInAsync(f);

        using var resp = await PostAsync(client, "/advanced/plan?handler=Change", new() { ["To"] = "simple-public" });

        Assert.Contains("Start your world first, so GridGate can check whether other computers have joined.", MainText(await resp.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(OwnerPlan.GridAsset, f.Store.LoadOrCreate().Plan);
    }

    [Fact]
    public async Task Leaving_a_grid_asset_server_is_saved_when_nobody_has_joined()
    {
        await using var f = Factory(OwnerPlan.GridAsset);
        f.Grid.JoinedRegions = 0;
        using var client = await SignedInAsync(f);

        using var resp = await PostAsync(client, "/advanced/plan?handler=Change", new() { ["To"] = "local" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(OwnerPlan.Local, f.Store.LoadOrCreate().Plan);
    }

    [Fact]
    public async Task Changes_that_do_not_leave_a_grid_asset_server_do_not_ask_for_the_count()
    {
        await using var f = Factory(OwnerPlan.Local);
        using var client = await SignedInAsync(f);

        (await PostAsync(client, "/advanced/plan?handler=Change", new() { ["To"] = "simple-public" })).Dispose();

        Assert.DoesNotContain("JoinedRegionCount", f.Grid.Snapshot());
    }

    [Fact]
    public async Task Region_server_plan_page_has_no_hypergrid_switch_and_links_to_the_join_page()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f, "owner");

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/advanced/plan", Ct));
        Assert.Null(doc.QuerySelector("form#hypergrid"));
        Assert.Contains("Hypergrid is off for this grid; the Grid Asset Server decides.", doc.QuerySelector("main")!.TextContent);
        Assert.Contains(doc.QuerySelectorAll("a"), a => a.GetAttribute("href") == "/advanced/join");
    }

    [Fact]
    public async Task Region_server_plan_page_shows_the_join_link_to_the_owner_only()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var admin = await SignedInAsync(f, "admin");

        var doc = new HtmlParser().ParseDocument(await admin.GetStringAsync("/advanced/plan", Ct));
        Assert.DoesNotContain(doc.QuerySelectorAll("a"), a => a.GetAttribute("href") == "/advanced/join");
    }

    [Fact]
    public async Task Region_server_plan_page_refuses_to_turn_hypergrid_on()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f);

        using var resp = await PostAsync(client, "/advanced/plan?handler=Hypergrid", new() { ["Hypergrid"] = "true" }, tokenFrom: "/advanced/plan");
        var text = MainText(await resp.Content.ReadAsStringAsync(Ct));

        Assert.False(f.Store.LoadOrCreate().Hypergrid);
        Assert.DoesNotContain("needs a public address", text);
        Assert.Contains("Hypergrid is off for this grid; the Grid Asset Server decides.", text);
    }

    // ---- Advanced: network ----

    [Fact]
    public async Task Region_server_network_page_says_the_grid_asset_server_decides_hypergrid()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f);

        var doc = new HtmlParser().ParseDocument(await client.GetStringAsync("/advanced/network", Ct));
        Assert.Contains("Hypergrid is off for this grid; the Grid Asset Server decides.", doc.QuerySelector("main")!.TextContent);
        Assert.Null(doc.QuerySelector("[name=Hypergrid]"));
    }

    [Fact]
    public async Task Local_network_page_has_no_hypergrid_line()
    {
        await using var f = Factory(OwnerPlan.Local);
        using var client = await SignedInAsync(f);

        Assert.DoesNotContain("the Grid Asset Server decides", MainText(await client.GetStringAsync("/advanced/network", Ct)));
    }

    [Fact]
    public async Task A_network_post_on_a_region_server_cannot_turn_hypergrid_on()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        using var client = await SignedInAsync(f);
        var s = f.Store.LoadOrCreate();
        var fields = new Dictionary<string, string>
        {
            ["Hostname"] = "", ["WebPort"] = s.WebPort.ToString(), ["RobustPublicPort"] = s.RobustPublicPort.ToString(),
            ["RobustPrivatePort"] = s.RobustPrivatePort.ToString(), ["RegionPort"] = s.RegionPort.ToString(), ["MariaDbPort"] = s.MariaDbPort.ToString(),
            ["RobustConsolePort"] = s.RobustConsolePort.ToString(), ["RegionConsolePort"] = s.RegionConsolePort.ToString(),
            ["Hypergrid"] = "true",
        };

        (await PostAsync(client, "/advanced/network", fields)).Dispose();

        var saved = f.Store.LoadOrCreate();
        Assert.False(saved.Hypergrid);
        Assert.False(PlanCatalog.HypergridOn(saved));
    }

    // ---- Advanced: join ----

    private const string NewPassword = "new-grid-pw1";
    private static readonly Guid NewOwnerId = Guid.Parse("50000000-0000-0000-0000-000000000009");

    /// <summary>The estate owner has an account with <see cref="NewPassword"/> on the new grid, and looking the owner up finds it.</summary>
    private static void SeedNewGridOwner(LauncherFactory f)
    {
        f.Robust.Add("Estate", "Owner", 0, NewPassword, NewOwnerId);
        f.Grid.EstateOwnerId = NewOwnerId;
    }

    [Fact]
    public async Task Join_page_confirm_saves_the_new_join_and_rewrites_the_config()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Avalon("Elsewhere") with { Host = "192.168.1.9", LoginUri = "http://192.168.1.9:8002/", Hypergrid = true };
        using var client = await SignedInAsync(f, "owner");

        using var first = await PostAsync(client, "/advanced/join", new() { ["Address"] = "192.168.1.9" });
        var doc = new HtmlParser().ParseDocument(await first.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Elsewhere", doc.QuerySelector("main")!.TextContent);
        Assert.Equal("192.168.1.5", f.Store.LoadOrCreate().Join!.Host); // nothing saved by the first read

        using var confirm = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.9", s.Join!.Host);
        Assert.Equal("Elsewhere", s.GridName);
        Assert.Equal(OwnerPlan.RegionServer, s.Plan);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
    }

    [Fact]
    public async Task Join_page_keeps_the_old_join_when_the_new_address_does_not_answer()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = null;
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");

        Assert.Contains("Nothing answered", MainText(await resp.Content.ReadAsStringAsync(Ct)));
        Assert.Equal("192.168.1.5", f.Store.LoadOrCreate().Join!.Host);
    }

    [Theory]
    [InlineData(OwnerPlan.Local)]
    [InlineData(OwnerPlan.GridAsset)]
    public async Task Join_page_redirects_to_advanced_on_another_plan(OwnerPlan plan)
    {
        await using var f = Factory(plan);
        using var client = await SignedInAsync(f, "owner");

        using var get = await client.GetAsync("/advanced/join", Ct);
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Equal("/advanced", get.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Join_page_confirm_moves_the_estate_owner_to_the_new_grids_account()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Avalon("Elsewhere") with { Host = "192.168.1.9", LoginUri = "http://192.168.1.9:8002/" };
        using var client = await SignedInAsync(f, "owner");

        (await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join")).Dispose();

        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.9", s.Join!.Host);
        Assert.Contains("SetEstateOwner Estate Owner", f.Grid.Snapshot());
        Assert.NotEqual(OwnerId, s.Setup.Owner!.PrincipalId);
    }

    [Fact]
    public async Task Join_page_confirm_puts_the_old_join_back_when_the_owner_is_not_on_the_new_grid()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Avalon("Elsewhere") with { Host = "192.168.1.9", LoginUri = "http://192.168.1.9:8002/" };
        f.Grid.EstateOwnerError = new ArgumentException("There's no avatar called Estate Owner on Elsewhere.");
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Contains("There's no avatar called Estate Owner on Elsewhere. Create your avatar on that grid first, then try again.", MainText(html));
        Assert.Contains(new HtmlParser().ParseDocument(html).QuerySelectorAll("button"), b => b.TextContent.Trim() == "Join this grid");
        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.5", s.Join!.Host);
        Assert.Equal("Avalon", s.GridName);
        Assert.Equal(OwnerId, s.Setup.Owner!.PrincipalId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Join_page_confirm_needs_the_owners_password_on_the_new_grid(string? password)
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        using var client = await SignedInAsync(f, "owner");
        var fields = new Dictionary<string, string> { ["Address"] = "192.168.1.9" };
        if (password is not null) fields["Password"] = password;
        var writes = f.Grid.UpdateWriteConfig.Count;

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm", fields, tokenFrom: "/advanced/join");
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Type your password on Elsewhere.", MainText(html));
        Assert.Contains(new HtmlParser().ParseDocument(html).QuerySelectorAll("button"), b => b.TextContent.Trim() == "Join this grid");
        Assert.Equal("192.168.1.5", f.Store.LoadOrCreate().Join!.Host);
        Assert.Equal(writes, f.Grid.UpdateWriteConfig.Count);
        Assert.DoesNotContain("SetEstateOwner", f.Grid.Snapshot());
    }

    [Fact]
    public async Task Join_page_confirm_with_a_wrong_password_puts_everything_back()
    {
        var pairing = new Core.Money.ServerPairing("192.168.1.5", new string('A', 64), "k", DateTimeOffset.UtcNow);
        await using var f = Factory(OwnerPlan.RegionServer, s => s with { Money = new Core.Money.MoneySettings(Pairing: pairing, Gloebit: SavedApp) });
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm",
            new() { ["Address"] = "192.168.1.9", ["Password"] = "not-the-password" }, tokenFrom: "/advanced/join");
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Contains("That isn't the password of Estate Owner on Elsewhere. Nothing was changed.", MainText(html));
        Assert.DoesNotContain("not-the-password", html);
        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.5", s.Join!.Host);
        Assert.Equal("Avalon", s.GridName);
        Assert.Equal(OwnerId, s.Setup.Owner!.PrincipalId);
        Assert.Equal(pairing, s.Money.Pairing);
        Assert.Equal(SavedApp, s.Money.Gloebit);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
        Assert.Equal("refused", AuditLines(f).Last(l => l.GetProperty("action").GetString() == "grid-join").GetProperty("result").GetString());
    }

    [Fact]
    public async Task Join_page_confirm_when_the_new_grid_does_not_answer_the_password_check()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        f.Grid.OnSetEstateOwner = () => f.Robust.Down = true; // the grid answers the owner lookup, then goes away
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm",
            new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");
        var html = await resp.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(GridAssetUnreachableException.MessageFor("192.168.1.9", Elsewhere().PublicPort), MainText(html));
        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.5", s.Join!.Host);
        Assert.Equal(OwnerId, s.Setup.Owner!.PrincipalId);
        Assert.True(f.Grid.UpdateWriteConfig.Last());
    }

    [Fact]
    public async Task Join_page_found_view_asks_for_the_password_and_never_shows_it_back()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword });
        var html = await resp.Content.ReadAsStringAsync(Ct);

        var input = new HtmlParser().ParseDocument(html).QuerySelector("input[name=Password][type=password]");
        Assert.NotNull(input);
        Assert.Null(input!.GetAttribute("value"));
        Assert.Equal("current-password", input.GetAttribute("autocomplete"));
        Assert.Contains("Your avatar's password on Elsewhere", MainText(html));
        Assert.DoesNotContain(NewPassword, html);
    }

    [Fact]
    public async Task Join_page_confirm_never_shows_the_password_back()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        f.Grid.EstateOwnerError = new ArgumentException("There's no avatar called Estate Owner on Elsewhere.");
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm",
            new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");

        Assert.DoesNotContain(NewPassword, await resp.Content.ReadAsStringAsync(Ct));
    }

    private static readonly GloebitSettings SavedApp = new("sandbox", "0b9e3c2a-1111-4c5d-8e9f-0123456789ab", "7d3f9a10-2222-4b6c-9d8e-fedcba987654", "Owner");

    private static List<System.Text.Json.JsonElement> AuditLines(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public async Task Join_page_is_the_owners_only()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        using var owner = await SignedInAsync(f, "owner");
        var page = await owner.GetStringAsync("/advanced/join", Ct);
        var token = new HtmlParser().ParseDocument(page).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;
        using var admin = await SignedInAsync(f, "admin");

        using var get = await admin.GetAsync("/advanced/join", Ct);
        using var post = await admin.PostAsync("/advanced/join?handler=Confirm",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["Address"] = "192.168.1.9", ["__RequestVerificationToken"] = token }), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal("192.168.1.5", f.Store.LoadOrCreate().Join!.Host);
    }

    [Fact]
    public async Task Join_page_confirm_to_another_host_clears_the_saved_gloebit_app()
    {
        await using var f = Factory(OwnerPlan.RegionServer, s => s with { Money = s.Money with { Gloebit = SavedApp } });
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        using var client = await SignedInAsync(f, "owner");

        (await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join")).Dispose();

        Assert.Equal("192.168.1.9", f.Store.LoadOrCreate().Join!.Host);
        Assert.Null(f.Store.LoadOrCreate().Money.Gloebit);
    }

    [Fact]
    public async Task Join_page_refused_re_point_keeps_the_saved_gloebit_app()
    {
        await using var f = Factory(OwnerPlan.RegionServer, s => s with { Money = s.Money with { Gloebit = SavedApp } });
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        f.Grid.EstateOwnerError = new ArgumentException("There's no avatar called Estate Owner on Elsewhere.");
        using var client = await SignedInAsync(f, "owner");

        (await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join")).Dispose();

        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.5", s.Join!.Host);
        Assert.Equal(SavedApp, s.Money.Gloebit);
    }

    [Fact]
    public async Task Join_page_confirm_writes_an_audit_line()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        using var client = await SignedInAsync(f, "owner");

        (await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join")).Dispose();
        f.Grid.EstateOwnerError = new ArgumentException("There's no avatar called Estate Owner on Elsewhere.");
        (await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join")).Dispose();

        var joins = AuditLines(f).Where(l => l.GetProperty("action").GetString() == "grid-join").ToList();
        Assert.Equal(2, joins.Count);
        Assert.All(joins, l => Assert.Equal("192.168.1.9", l.GetProperty("target").GetString()));
        Assert.Equal(["ok", "refused"], joins.Select(l => l.GetProperty("result").GetString()));
    }

    private static readonly Guid HomeId = Guid.Parse("40000000-0000-0000-0000-000000000001");

    /// <summary>A Region Server with one region, Home, at 1000, 1000, whose first region port is <paramref name="regionPort"/>.</summary>
    private static LauncherFactory WithHome(int regionPort = 9000) => Factory(OwnerPlan.RegionServer, s => s with
    {
        RegionPort = regionPort,
        Regions = [new RegionSettings("Home", "Home", HomeId, UdpPort: regionPort)],
    });

    private static JoinedGrid Elsewhere(int portsFrom = 9100, int portsTo = 9110) =>
        Avalon("Elsewhere") with { Host = "192.168.1.9", LoginUri = "http://192.168.1.9:8002/", RegionPortsFrom = portsFrom, RegionPortsTo = portsTo };

    [Fact]
    public async Task Join_page_confirm_is_refused_when_a_region_would_overlap_one_on_the_new_grid()
    {
        await using var f = WithHome();
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        f.Robust.MapRegions.Add(new MapRegion(HomeId, "Home", 1000, 1000, 1)); // ours: never in the way
        f.Robust.MapRegions.Add(new MapRegion(Guid.NewGuid(), "Hub", 1000, 1000, 1));
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");

        Assert.Contains("Your region Home would overlap Hub on Elsewhere. Move it first (Regions page), then try again.",
            MainText(await resp.Content.ReadAsStringAsync(Ct)));
        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.5", s.Join!.Host);
        Assert.Equal("Avalon", s.GridName);
        Assert.Equal(OwnerId, s.Setup.Owner!.PrincipalId);
    }

    [Fact]
    public async Task Refused_repoint_keeps_the_money_pairing()
    {
        var pairing = new Core.Money.ServerPairing("192.168.1.5", new string('A', 64), "k", DateTimeOffset.UtcNow);
        await using var f = Factory(OwnerPlan.RegionServer, s => s with
        {
            RegionPort = 9000,
            Regions = [new RegionSettings("Home", "Home", HomeId, UdpPort: 9000)],
            Money = new Core.Money.MoneySettings(Pairing: pairing),
        });
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        f.Robust.MapRegions.Add(new MapRegion(Guid.NewGuid(), "Hub", 1000, 1000, 1));
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");

        Assert.Contains("would overlap Hub", MainText(await resp.Content.ReadAsStringAsync(Ct)));
        Assert.Equal(pairing, f.Store.LoadOrCreate().Money.Pairing);
    }

    [Fact]
    public async Task Join_page_confirm_is_saved_when_no_region_overlaps()
    {
        await using var f = WithHome();
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        f.Robust.MapRegions.Add(new MapRegion(HomeId, "Home", 1000, 1000, 1));
        f.Robust.MapRegions.Add(new MapRegion(Guid.NewGuid(), "Hub", 1001, 1000, 1));
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");

        var text = MainText(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Saved.", text);
        Assert.DoesNotContain("region ports overlap", text);
        var s = f.Store.LoadOrCreate();
        Assert.Equal("192.168.1.9", s.Join!.Host);
        Assert.Equal("Elsewhere", s.GridName);
    }

    [Fact]
    public async Task Join_page_confirm_warns_when_region_ports_overlap_the_new_grids()
    {
        await using var f = WithHome(regionPort: 8005);
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere(8004, 8006);
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join?handler=Confirm", new() { ["Address"] = "192.168.1.9", ["Password"] = NewPassword }, tokenFrom: "/advanced/join");

        var text = MainText(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Saved.", text);
        Assert.Contains("This computer's region ports overlap the Grid Asset Server's (8004-8006). If both computers are behind one router, "
            + "change this computer's first region port in Settings → Network.", text);
        Assert.Equal("192.168.1.9", f.Store.LoadOrCreate().Join!.Host);
    }

    [Fact]
    public async Task Join_page_confirm_view_says_the_regions_keep_their_land()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        SeedNewGridOwner(f);
        f.Join.Result = Elsewhere();
        using var client = await SignedInAsync(f, "owner");

        using var resp = await PostAsync(client, "/advanced/join", new() { ["Address"] = "192.168.1.9" });

        var text = MainText(await resp.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Your regions keep their land, objects and estate. Things made on the old grid may lose their textures on the new one, "
            + "because those are kept by the Grid Asset Server.", text);
    }

    [Fact]
    public async Task Region_server_dashboard_shows_no_robust_log_for_a_failed_grid_asset_server()
    {
        await using var f = Factory(OwnerPlan.RegionServer);
        f.Grid.Status = RegionServerStatus(ProcessState.Failed);
        Directory.CreateDirectory(f.Paths.Logs);
        File.WriteAllText(Path.Combine(f.Paths.Logs, "robust.log"), "2026-10-25 [ERROR] STALE-ROBUST-LINE\n");
        using var client = await SignedInAsync(f);

        var html = await client.GetStringAsync("/", Ct);

        Assert.DoesNotContain("STALE-ROBUST-LINE", html);
        Assert.Contains("The Grid Asset Server isn&#x27;t answering.", html);
    }
}
