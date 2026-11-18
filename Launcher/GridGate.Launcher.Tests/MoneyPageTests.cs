using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The ledger, faked for the money pages: answers what a test sets and records every call.</summary>
internal sealed class FakeBooks : IMoneyBooks
{
    public List<string> Calls { get; } = [];
    public List<StaffAction> Staff { get; } = [];
    public (bool Frozen, string Reason) Frozen { get; set; } = (false, "");
    public bool Down { get; set; }
    public long Balance { get; set; } = 100;
    public AccountFlags? Flags { get; set; } = new(false, false);
    public List<HistoryLine> History { get; } = [];
    public MoneyOutcome Outcome { get; set; } = MoneyOutcome.Ok;
    public SelfCheckResult Check { get; set; } = new(true, 0, [], []);

    private void Up(string call)
    {
        lock (Calls) Calls.Add(call);
        if (Down) throw new MoneyUnavailableException(new InvalidOperationException("down"));
    }

    private Task<MoneyResult> Moved(string call, StaffAction? who = null)
    {
        Up(call);
        if (who is not null) lock (Staff) Staff.Add(who);
        return Task.FromResult(Outcome == MoneyOutcome.Ok ? new MoneyResult(MoneyOutcome.Ok, Balance, Balance, 1, null) : MoneyResult.Of(Outcome));
    }

    public Task<MoneyResult> BalanceAsync(AccountRef who, CancellationToken ct) => Moved($"balance {who.AgentId}");
    /// <summary>What <see cref="FindBalanceAsync"/> answers: Ok with <see cref="Balance"/> unless set.</summary>
    public MoneyOutcome FindOutcome { get; set; } = MoneyOutcome.Ok;
    public Task<MoneyResult> FindBalanceAsync(AccountRef who, CancellationToken ct)
    {
        Up($"find {who.AgentId}");
        return Task.FromResult(FindOutcome == MoneyOutcome.Ok ? new MoneyResult(MoneyOutcome.Ok, Balance) : MoneyResult.Of(FindOutcome));
    }
    public Task<MoneyResult> MergeWalletsAsync(AccountRef from, AccountRef to, StaffAction who, CancellationToken ct) =>
        Moved($"merge {from.AgentId} {from.Home} -> {to.Home}", who);
    public Task<IReadOnlyList<HistoryLine>> HistoryAsync(AccountRef who, int max, CancellationToken ct)
    {
        Up($"history {who.AgentId} {max}");
        return Task.FromResult<IReadOnlyList<HistoryLine>>(History);
    }
    public Task<AccountFlags?> AccountFlagsAsync(AccountRef who, CancellationToken ct)
    {
        Up($"flags {who.AgentId}");
        return Task.FromResult(Flags);
    }
    public Task<MoneyTotals> TotalsAsync(CancellationToken ct)
    {
        Up("totals");
        return Task.FromResult(new MoneyTotals(1000, 900, 50, 30, 20, 7));
    }
    public Task<(bool Frozen, string Reason)> FrozenAsync(CancellationToken ct)
    {
        Up("frozen?");
        return Task.FromResult(Frozen);
    }
    public Task SetFrozenAsync(bool frozen, string reason, CancellationToken ct)
    {
        Up($"freeze {frozen} {reason}");
        Frozen = (frozen, frozen ? reason : "");
        return Task.CompletedTask;
    }
    public Task SetAccountFrozenAsync(AccountRef who, bool frozen, CancellationToken ct)
    {
        Up($"freeze-account {who.AgentId} {frozen}");
        return Task.CompletedTask;
    }
    public Task<MoneyResult> GiveAsync(AccountRef to, long amount, StaffAction who, CancellationToken ct) => Moved($"give {to.AgentId} {amount}", who);
    public Task<MoneyResult> TakeAsync(AccountRef from, long amount, StaffAction who, CancellationToken ct) => Moved($"take {from.AgentId} {amount}", who);
    public Task<MoneyResult> MoveFeesAsync(AccountRef to, long amount, StaffAction who, CancellationToken ct) => Moved($"move-fees {to.AgentId} {amount}", who);
    public Task<MoneyResult> RefundAsync(long entryId, StaffAction who, CancellationToken ct) => Moved($"refund {entryId}", who);
    public Task<MoneyResult> SetPayoutsAllowedAsync(Guid residentId, bool allowed, CancellationToken ct) => Moved($"payouts {residentId} {allowed}");
    public Task<SelfCheckResult> SelfCheckAsync(CancellationToken ct)
    {
        Up("self-check");
        return Task.FromResult(Check);
    }
    /// <summary>What the limit reads answer.</summary>
    public LimitView? Limits { get; set; } = new(10_000, 5_000, false, false, null, 0, 0);
    public Task<LimitView?> LimitsAsync(LimitTarget target, CancellationToken ct)
    {
        Up($"limits {target.Kind} {target.Id}");
        return Task.FromResult(Limits);
    }
    public Task<LimitView?> PersonLimitsAsync(AccountRef person, CancellationToken ct)
    {
        Up($"person-limits {person.AgentId}");
        return Task.FromResult(Limits);
    }
    public Task<MoneyOutcome> SetLimitsAsync(LimitTarget target, long? dailySpend, long? dailyPayout, DateTime? until, StaffAction staff, CancellationToken ct)
    {
        Up($"set-limits {target.Kind} {target.Id} {dailySpend} {dailyPayout} {until:O}");
        lock (Staff) Staff.Add(staff);
        return Task.FromResult(Outcome);
    }
    public Task<MoneyOutcome> SetPersonLimitsAsync(AccountRef person, long? dailySpend, long? dailyPayout, DateTime? until, StaffAction staff, CancellationToken ct)
    {
        Up($"set-person-limits {person.AgentId} {dailySpend} {dailyPayout} {until:O}");
        lock (Staff) Staff.Add(staff);
        return Task.FromResult(Outcome);
    }
}

/// <summary>The owner's money alerts, faked for the pages: <see cref="Open"/> is what is open. <see cref="ClearDown"/> makes clearing a database failure.</summary>
internal sealed class FakeAlerts : IMoneyAlerts
{
    public List<MoneyAlert> Open { get; } = [];
    public List<string> Calls { get; } = [];
    public bool ClearDown { get; set; }

    public Task<IReadOnlyList<MoneyAlert>> OpenAsync(CancellationToken ct)
    {
        lock (Calls) Calls.Add("open");
        return Task.FromResult<IReadOnlyList<MoneyAlert>>(Open.ToList());
    }

    public Task<IReadOnlyList<MoneyAlert>> WarningsAsync(CancellationToken ct)
    {
        lock (Calls) Calls.Add("warnings");
        return Task.FromResult<IReadOnlyList<MoneyAlert>>(Open.Where(a => a.Level == AlertLevel.Warning).ToList());
    }

    public Task ClearAsync(long id, string staff, CancellationToken ct)
    {
        lock (Calls) Calls.Add($"clear {id} {staff}");
        if (ClearDown) throw new MoneyUnavailableException(new InvalidOperationException("down"));
        Open.RemoveAll(a => a.Id == id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The regions' money status and where people are, faked for the pages (every <see cref="LauncherFactory"/> has one):
/// answers from what a test sets and records each approval. <see cref="Down"/> makes every call a database failure.
/// </summary>
internal sealed class FakeMoneyRegions : IMoneyRegions
{
    public List<RegionState> States { get; } = [];
    public Dictionary<string, KeyHeard> Keys { get; } = [];
    public Dictionary<Guid, SessionLocation> Where { get; } = [];
    public List<string> Calls { get; } = [];
    public bool Down { get; set; }

    private void Up(string call)
    {
        lock (Calls) Calls.Add(call);
        if (Down) throw new MoneyUnavailableException(new InvalidOperationException("down"));
    }

    public Task<IReadOnlyList<RegionState>> AllAsync(LauncherSettings settings, CancellationToken ct)
    {
        Up("all");
        return Task.FromResult<IReadOnlyList<RegionState>>(States.ToList());
    }

    public Task<IReadOnlyDictionary<string, KeyHeard>> KeysAsync(CancellationToken ct)
    {
        Up("keys");
        return Task.FromResult<IReadOnlyDictionary<string, KeyHeard>>(new Dictionary<string, KeyHeard>(Keys));
    }

    public Task ApproveMoveAsync(Guid regionId, Guid toServerId, CancellationToken ct)
    {
        Up($"approve {regionId} {toServerId}");
        return Task.CompletedTask;
    }

    public Task<int> ConfirmWaitingAsync(PairedServer server, int fromPort, int toPort, IReadOnlySet<Guid> ownRegions, CancellationToken ct)
    {
        Up($"confirm {server.Id}");
        return Task.FromResult(Waiting(server.Id).Count);
    }

    public Task<IReadOnlyList<Guid>> WaitingAsync(PairedServer server, int fromPort, int toPort, IReadOnlySet<Guid> ownRegions, CancellationToken ct)
    {
        Up($"waiting {server.Id}");
        return Task.FromResult<IReadOnlyList<Guid>>(Waiting(server.Id));
    }

    private List<Guid> Waiting(Guid serverId) =>
        States.Where(r => r.Status == RegionStatus.Waiting && r.KeyId == "server:" + serverId.ToString("N")).Select(r => r.RegionId).ToList();

    public Task<SessionLocation?> WhereAgentAsync(Guid agentId, CancellationToken ct)
    {
        Up($"where {agentId}");
        return Task.FromResult(Where.GetValueOrDefault(agentId));
    }
}

/// <summary>Robust's record of hypergrid visitors, faked for the money pages: answers from <see cref="Known"/>.</summary>
internal sealed class FakeVisitors : IVisitorDirectory
{
    public List<VisitorInfo> Known { get; } = [];
    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<VisitorInfo>> SearchAsync(string text, int max, CancellationToken ct)
    {
        Calls.Add($"search {text} {max}");
        return Task.FromResult<IReadOnlyList<VisitorInfo>>(Known.Where(v => v.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(max).ToList());
    }

    public Task<VisitorInfo?> FindAsync(Guid id, string home, CancellationToken ct) =>
        Task.FromResult(Known.FirstOrDefault(v => v.Id == id && MoneyHomes.Normal(v.Home) == MoneyHomes.Normal(home)));
}

/// <summary>The Money staff page, the person page and the dashboard's money notices (money plan 2, task 9).</summary>
public sealed class MoneyPageTests
{
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static readonly Guid Hope = Guid.Parse("c0000000-0000-0000-0000-0000000000c1");
    private const string HopeHome = "http://grid.example:8002/";

    private static LauncherFactory Make(FakeBooks books, bool open = true, Func<MoneySettings, MoneySettings>? money = null, FakeVisitors? visitors = null,
        OwnerPlan plan = OwnerPlan.Local, FakeAlerts? alerts = null, Func<LauncherSettings, LauncherSettings>? more = null)
    {
        var f = new LauncherFactory(s => (more ?? (x => x))(s with
        {
            SetupToken = null,
            Plan = plan,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Money = money?.Invoke(new MoneySettings(Mode: MoneyMode.Play)) ?? new MoneySettings(Mode: MoneyMode.Play),
            MoneyPort = 0,
        }))
        {
            Before = TestAuth.Apply,
            ExtraServices = services =>
            {
                services.RemoveAll<IMoneyBooks>();
                services.AddSingleton<IMoneyBooks>(books);
                services.RemoveAll<IVisitorDirectory>();
                services.AddSingleton<IVisitorDirectory>(visitors ?? new FakeVisitors());
                services.RemoveAll<IMoneyAlerts>();
                services.AddSingleton<IMoneyAlerts>(alerts ?? new FakeAlerts());
                if (open)
                {
                    services.RemoveAll<MoneyGate>();
                    services.AddSingleton(sp => new MoneyGate(sp.GetRequiredService<Func<LauncherSettings>>(), MoneyGate.Open));
                }
            },
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Smith", 0, "pw", Ann);
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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string page, string handler, Dictionary<string, string> fields)
    {
        var doc = await PageAsync(c, page);
        var token = doc.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        var sep = page.Contains('?') ? '&' : '?';
        return await c.PostAsync($"{page}{sep}handler={handler}", new FormUrlEncodedContent(form), Ct);
    }

    private static async Task<IHtmlDocument> ParseAsync(HttpResponseMessage r) => Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));

    private static Dictionary<string, string> Settings(string symbol = "C$", string mode = "Play") => new()
    {
        ["Mode"] = mode, ["CurrencySymbol"] = symbol, ["CurrencyName"] = "coins", ["StartingBalancePlay"] = "100", ["StartingBalanceReal"] = "0",
        ["WeeklyAllowance"] = "0", ["UploadFee"] = "0", ["ClassifiedFee"] = "0", ["DailyPayoutLimit"] = "5000", ["MaxPayment"] = "1000000",
        ["ScriptPayouts"] = "true", ["DailySpendLimit"] = "10000", ["DailyKeySpendLimit"] = "100000", ["DailyKeyPayoutLimit"] = "20000",
    };

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
    }

    private static string MainText(IHtmlDocument d) => d.QuerySelector("main")!.TextContent;

    [Fact]
    public async Task Closed_gate_offers_only_off()
    {
        await using var f = Make(new FakeBooks(), open: false);

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        var options = doc.QuerySelectorAll("select[name=Mode] option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(["Off"], options);
        Assert.Contains("Play coins become available once the money safety review is approved.", MainText(doc));
    }

    [Fact]
    public async Task Open_gate_offers_off_play_and_gloebit()
    {
        await using var f = Make(new FakeBooks());

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        Assert.Equal(["Off", "Play", "Gloebit"], doc.QuerySelectorAll("select[name=Mode] option").Select(o => o.GetAttribute("value")));
    }

    [Fact]
    public async Task Settings_save_and_audit()
    {
        await using var f = Make(new FakeBooks());
        var c = await SignedInAsync(f);

        using var r = await PostAsync(c, "/money", "Settings", Settings(symbol: "GG"));

        Assert.Equal("GG", f.Store.LoadOrCreate().Money.CurrencySymbol);
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-settings");
        Assert.Contains(true, f.Grid.UpdateWriteConfig);
    }

    [Fact]
    public async Task Mode_change_says_it_needs_a_restart()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Off });
        var c = await SignedInAsync(f);

        using var r = await PostAsync(c, "/money", "Settings", Settings(mode: "Play"));

        Assert.Equal(MoneyMode.Play, f.Store.LoadOrCreate().Money.Mode);
        Assert.Contains("Takes effect after you restart GridGate and then the world (Power → Restart on the dashboard).", MainText(await ParseAsync(r)));
    }

    [Theory]
    [InlineData("TOOLONG")]
    [InlineData("")]
    [InlineData("C $")]
    [InlineData("C;")]
    public async Task Bad_symbol_is_refused(string symbol)
    {
        await using var f = Make(new FakeBooks());
        var c = await SignedInAsync(f);

        using var r = await PostAsync(c, "/money", "Settings", Settings(symbol: symbol));

        Assert.Equal("C$", f.Store.LoadOrCreate().Money.CurrencySymbol);
        Assert.NotNull((await ParseAsync(r)).QuerySelector(".alert-danger"));
    }

    [Fact]
    public void The_symbol_error_lists_every_character_the_rule_refuses()
    {
        var text = MoneyPageRules.Problem(new MoneySettings(CurrencySymbol: "{C}"), [MoneyMode.Off]);

        Assert.NotNull(text);
        foreach (var c in MoneySymbolRule.ForbiddenCharacters) Assert.Contains(c.ToString(), text);
        Assert.Contains("#", MoneySymbolRule.ForbiddenCharacters.Select(c => c.ToString()));
        Assert.Contains("{", MoneySymbolRule.ForbiddenCharacters.Select(c => c.ToString()));
        Assert.Contains("}", MoneySymbolRule.ForbiddenCharacters.Select(c => c.ToString()));
    }

    [Fact]
    public async Task The_gloebit_help_says_only_the_masked_key_appears()
    {
        await using var f = Make(new FakeBooks());

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        Assert.Contains("last four characters of the app key", doc.QuerySelector("#money-gloebit-title")!.TextContent);
    }

    [Theory]
    [InlineData("MaxPayment", "0")]
    [InlineData("WeeklyAllowance", "-1")]
    [InlineData("UploadFee", "2000000")]
    [InlineData("DailyPayoutLimit", "100000001")]
    [InlineData("CurrencyName", "")]
    public async Task Out_of_range_numbers_are_refused(string field, string value)
    {
        await using var f = Make(new FakeBooks());
        var c = await SignedInAsync(f);
        var form = Settings();
        form[field] = value;

        using var r = await PostAsync(c, "/money", "Settings", form);

        Assert.Equal(new MoneySettings(Mode: MoneyMode.Play), f.Store.LoadOrCreate().Money with { ProgramKeys = null });
        Assert.NotNull((await ParseAsync(r)).QuerySelector(".alert-danger"));
    }

    [Fact]
    public async Task Real_mode_is_not_accepted_here()
    {
        await using var f = Make(new FakeBooks());
        var c = await SignedInAsync(f);

        using var r = await PostAsync(c, "/money", "Settings", Settings(mode: "Real"));

        Assert.Equal(MoneyMode.Play, f.Store.LoadOrCreate().Money.Mode);
    }

    [Fact]
    public async Task Program_keys_survive_a_settings_save()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { ProgramKeys = new() { ["p1"] = "the-key" } });
        var c = await SignedInAsync(f);

        using var r = await PostAsync(c, "/money", "Settings", Settings(symbol: "GG"));

        Assert.Equal("the-key", f.Store.LoadOrCreate().Money.Keys["p1"]);
    }

    [Fact]
    public async Task Supervisor_sees_no_forms_and_gets_403()
    {
        var books = new FakeBooks();
        await using var f = Make(books);
        var c = await SignedInAsync(f, "supervisor");

        var doc = await PageAsync(c, "/money");
        Assert.Empty(doc.QuerySelectorAll("main form[method=post]"));
        Assert.Contains("Totals", MainText(doc));
        Assert.Empty(doc.QuerySelectorAll("main form[method=post]"));
        Assert.Empty((await PageAsync(c, $"/money/person?id={Ann}")).QuerySelectorAll("main form[method=post]"));

        using var r = await PostAsync(c, "/money", "Freeze", new() { ["Reason"] = "x" });
        using var g = await PostAsync(c, $"/money/person?id={Ann}", "Give", new() { ["Amount"] = "5", ["Reason"] = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, g.StatusCode);
        Assert.DoesNotContain(books.Calls, x => x.StartsWith("freeze") || x.StartsWith("give"));
    }

    [Fact]
    public async Task Residents_cannot_open_it()
    {
        await using var f = Make(new FakeBooks());
        var c = f.CreateClient(new() { AllowAutoRedirect = false });

        using var r = await c.GetAsync("/money", Ct);

        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Ledger_sections_hidden_when_off()
    {
        var books = new FakeBooks();
        await using var f = Make(books, open: false);

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        Assert.DoesNotContain("Totals", MainText(doc));
        Assert.Empty(books.Calls);
    }

    [Fact]
    public async Task Totals_and_last_check_are_shown()
    {
        await using var f = Make(new FakeBooks());
        f.Services.GetRequiredService<MoneyStatus>().LastCheck = (f.Time.GetUtcNow(), new SelfCheckResult(true, 0, [], []));

        var text = MainText(await PageAsync(await SignedInAsync(f), "/money"));

        Assert.Contains("1,000", text);
        Assert.Contains("The books add up.", text);
    }

    [Fact]
    public async Task What_the_last_check_found_is_listed()
    {
        await using var f = Make(new FakeBooks());
        f.Services.GetRequiredService<MoneyStatus>().LastCheck = (f.Time.GetUtcNow(),
            new SelfCheckResult(false, 5, [new BalanceMismatch(7, 75, 70)], ["Hold 3 doesn't match its entries."]));

        var text = MainText(await PageAsync(await SignedInAsync(f), "/money"));

        Assert.Contains("The last check found a difference.", text);
        Assert.Contains("Account 7 has 75, but its entries add up to 70.", text);
        Assert.Contains("All balances add up to 5 instead of zero.", text);
        Assert.Contains("Hold 3 doesn't match its entries.", text);
    }

    [Fact]
    public async Task Check_now_runs_the_self_check_and_audits()
    {
        var books = new FakeBooks();
        await using var f = Make(books);

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Check", []);

        Assert.Contains("self-check", books.Calls);
        Assert.NotNull(f.Services.GetRequiredService<MoneyStatus>().LastCheck);
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-self-check");
    }

    [Fact]
    public async Task Freeze_all_needs_a_reason_and_is_audited()
    {
        var books = new FakeBooks();
        await using var f = Make(books);
        var c = await SignedInAsync(f);

        using var none = await PostAsync(c, "/money", "Freeze", new() { ["Reason"] = " " });
        Assert.DoesNotContain(books.Calls, x => x.StartsWith("freeze"));

        using var r = await PostAsync(c, "/money", "Freeze", new() { ["Reason"] = "checking a report" });
        Assert.Contains("freeze True checking a report", books.Calls);
        using var u = await PostAsync(c, "/money", "Unfreeze", new() { ["Reason"] = "done" });

        var actions = Audit(f).Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("money-freeze-all", actions);
        Assert.Contains("money-unfreeze-all", actions);
    }

    [Fact]
    public async Task Refund_through_the_page()
    {
        var books = new FakeBooks();
        await using var f = Make(books);

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Refund", new() { ["EntryId"] = "42", ["Reason"] = "wrong item" });

        Assert.Contains("refund 42", books.Calls);
        Assert.Equal(new StaffAction("Test Owner", "wrong item"), Assert.Single(books.Staff));
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-refund");
        Assert.Equal("42", line.GetProperty("target").GetString());
        Assert.Equal("ok", line.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Move_fees_finds_the_resident_by_name()
    {
        var books = new FakeBooks();
        await using var f = Make(books);

        using var r = await PostAsync(await SignedInAsync(f), "/money", "MoveFees", new() { ["Name"] = "Ann Smith", ["Amount"] = "30", ["Reason"] = "prize" });

        Assert.Contains($"move-fees {Ann} 30", books.Calls);
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-move-fees" && a.GetProperty("target").GetString() == "Ann Smith");
    }

    [Fact]
    public async Task Give_then_take_through_the_page()
    {
        var books = new FakeBooks();
        await using var f = Make(books);
        var c = await SignedInAsync(f);

        using var give = await PostAsync(c, $"/money/person?id={Ann}", "Give", new() { ["Amount"] = "25", ["Reason"] = "welcome gift" });
        using var take = await PostAsync(c, $"/money/person?id={Ann}", "Take", new() { ["Amount"] = "5", ["Reason"] = "mistake" });

        Assert.Contains($"give {Ann} 25", books.Calls);
        Assert.Contains($"take {Ann} 5", books.Calls);
        Assert.Equal([new StaffAction("Test Owner", "welcome gift"), new StaffAction("Test Owner", "mistake")], books.Staff);
        var lines = Audit(f).Where(a => a.GetProperty("action").GetString() is "money-give" or "money-take").ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("Ann Smith", l.GetProperty("target").GetString()));
    }

    [Fact]
    public async Task Refused_give_is_shown_and_audited_with_the_outcome()
    {
        var books = new FakeBooks { Outcome = MoneyOutcome.Frozen };
        await using var f = Make(books);

        using var r = await PostAsync(await SignedInAsync(f), $"/money/person?id={Ann}", "Give", new() { ["Amount"] = "25", ["Reason"] = "gift" });

        Assert.NotNull((await ParseAsync(r)).QuerySelector(".alert-danger"));
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-give" && a.GetProperty("result").GetString() == "frozen 25");
    }

    [Fact]
    public async Task Opening_a_person_creates_no_wallet()
    {
        var books = new FakeBooks();
        await using var f = Make(books);

        await PageAsync(await SignedInAsync(f), $"/money/person?id={Ann}");

        Assert.Contains($"find {Ann}", books.Calls);
        Assert.DoesNotContain(books.Calls, c => c.StartsWith("balance"));
    }

    [Fact]
    public async Task Resident_without_wallet_says_so()
    {
        var books = new FakeBooks { FindOutcome = MoneyOutcome.NoWallet };
        await using var f = Make(books);

        var text = MainText(await PageAsync(await SignedInAsync(f), $"/money/person?id={Ann}"));

        Assert.Contains("No wallet yet. Giving coins makes one.", text);
    }

    [Fact]
    public async Task Made_up_visitor_home_is_404()
    {
        var books = new FakeBooks { FindOutcome = MoneyOutcome.NoWallet };
        await using var f = Make(books);
        var c = await SignedInAsync(f);

        using var r = await c.GetAsync($"/money/person?id={Hope}&home=" + Uri.EscapeDataString("http://made.up/"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.DoesNotContain(books.Calls, x => x.StartsWith("balance") || x.StartsWith("give"));
    }

    [Fact]
    public async Task Known_visitor_without_wallet_is_shown()
    {
        var visitors = new FakeVisitors();
        visitors.Known.Add(new VisitorInfo(Hope, HopeHome, "Hope Visitor"));
        await using var f = Make(new FakeBooks { FindOutcome = MoneyOutcome.NoWallet }, visitors: visitors);

        var text = MainText(await PageAsync(await SignedInAsync(f), $"/money/person?id={Hope}&home=" + Uri.EscapeDataString("http://GRID.example:8002")));

        Assert.Contains("Hope Visitor (visitor from grid.example)", text);
        Assert.Contains("No wallet yet. Giving coins makes one.", text);
    }

    [Fact]
    public async Task Visitor_with_a_wallet_but_no_record_is_still_shown()
    {
        await using var f = Make(new FakeBooks { Balance = 7 });

        var text = MainText(await PageAsync(await SignedInAsync(f), $"/money/person?id={Hope}&home=" + Uri.EscapeDataString(HopeHome)));

        Assert.Contains("Visitor from grid.example", text);
        Assert.Contains("7", text);
    }

    [Fact]
    public async Task Find_shows_visitors()
    {
        var visitors = new FakeVisitors();
        visitors.Known.Add(new VisitorInfo(Hope, HopeHome, "Hope Visitor"));
        await using var f = Make(new FakeBooks(), visitors: visitors);

        var doc = await PageAsync(await SignedInAsync(f), "/money?Q=Hope");

        var link = Assert.Single(doc.QuerySelectorAll("#money-found a"), a => a.TextContent.Contains("Hope Visitor"));
        Assert.Equal("Hope Visitor (visitor from grid.example)", link.TextContent.Trim());
        Assert.Equal($"/money/person?id={Hope}&home=" + Uri.EscapeDataString(HopeHome), link.GetAttribute("href"));
        Assert.Contains("search Hope 50", visitors.Calls);
    }

    [Fact]
    public async Task Merge_is_audited_and_admin_only()
    {
        var books = new FakeBooks();
        var visitors = new FakeVisitors();
        visitors.Known.Add(new VisitorInfo(Hope, HopeHome, "Hope Visitor"));
        await using var f = Make(books, visitors: visitors);
        var page = $"/money/person?id={Hope}&home=" + Uri.EscapeDataString(HopeHome);

        var supervisor = await SignedInAsync(f, "supervisor");
        using (var r = await supervisor.PostAsync(page + "&handler=Merge", new FormUrlEncodedContent(new Dictionary<string, string>()), Ct))
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain(books.Calls, x => x.StartsWith("merge"));

        using var ok = await PostAsync(await SignedInAsync(f), page, "Merge", new() { ["NewHome"] = "http://new.example:8002/", ["Reason"] = "grid moved" });

        Assert.Contains($"merge {Hope} {HopeHome} -> http://new.example:8002/", books.Calls);
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-merge-wallet"
            && a.GetProperty("target").GetString() == "Hope Visitor (visitor from grid.example)" && a.GetProperty("result").GetString() == "ok");
    }

    [Fact]
    public async Task Merge_needs_a_home_and_a_reason()
    {
        var books = new FakeBooks();
        var visitors = new FakeVisitors();
        visitors.Known.Add(new VisitorInfo(Hope, HopeHome, "Hope Visitor"));
        await using var f = Make(books, visitors: visitors);

        using var r = await PostAsync(await SignedInAsync(f), $"/money/person?id={Hope}&home=" + Uri.EscapeDataString(HopeHome), "Merge",
            new() { ["NewHome"] = "", ["Reason"] = "x" });

        Assert.NotNull((await ParseAsync(r)).QuerySelector(".alert-danger"));
        Assert.DoesNotContain(books.Calls, x => x.StartsWith("merge"));
    }

    [Fact]
    public async Task Residents_get_no_merge_form()
    {
        await using var f = Make(new FakeBooks());

        var doc = await PageAsync(await SignedInAsync(f), $"/money/person?id={Ann}");

        Assert.Empty(doc.QuerySelectorAll("form[action*='handler=Merge']"));
    }

    [Fact]
    public async Task Audit_lines_carry_the_amount()
    {
        await using var f = Make(new FakeBooks());

        using var r = await PostAsync(await SignedInAsync(f), $"/money/person?id={Ann}", "Give", new() { ["Amount"] = "25", ["Reason"] = "gift" });

        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-give" && a.GetProperty("result").GetString() == "ok 25");
    }

    [Fact]
    public async Task Pairing_code_shown_once_and_audited_without_it()
    {
        await using var f = Make(new FakeBooks(), plan: OwnerPlan.GridAsset);
        var c = await SignedInAsync(f);

        using var made = await PostAsync(c, "/money", "PairingCode", []);
        var code = (await ParseAsync(made)).QuerySelector("#money-pairing-code code")?.TextContent.Trim();

        Assert.NotNull(code);
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", code);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-pairing-code");
        Assert.DoesNotContain(code!.Replace("-", ""), line.GetRawText().Replace("-", ""));
        var again = await PageAsync(c, "/money");
        Assert.Null(again.QuerySelector("#money-pairing-code"));
        Assert.NotNull(again.QuerySelector("#money-pairing-waiting"));
        Assert.DoesNotContain(code, again.DocumentElement.OuterHtml);
    }

    [Fact]
    public async Task Region_servers_section_only_on_grid_asset()
    {
        await using var local = Make(new FakeBooks());
        Assert.Null((await PageAsync(await SignedInAsync(local), "/money")).QuerySelector("#money-servers"));

        await using var gas = Make(new FakeBooks(), plan: OwnerPlan.GridAsset);
        Assert.NotNull((await PageAsync(await SignedInAsync(gas), "/money")).QuerySelector("#money-servers"));

        await using var closed = Make(new FakeBooks(), open: false, plan: OwnerPlan.GridAsset);
        Assert.Null((await PageAsync(await SignedInAsync(closed), "/money")).QuerySelector("#money-servers"));
    }

    [Fact]
    public async Task Pairing_code_refused_off_grid_asset()
    {
        await using var f = Make(new FakeBooks());

        using var r = await PostAsync(await SignedInAsync(f), "/money", "PairingCode", []);

        Assert.Null((await ParseAsync(r)).QuerySelector("#money-pairing-code"));
        Assert.Null(f.Services.GetRequiredService<MoneyPairing>().PendingUntil);
    }

    [Fact]
    public async Task Remove_paired_server_is_admin_only_and_audited()
    {
        var id = Guid.NewGuid();
        await using var f = Make(new FakeBooks(), plan: OwnerPlan.GridAsset,
            money: m => m with { PairedServers = [new PairedServer(id, "192.168.1.20", new string('A', 64), DateTimeOffset.UtcNow)] });
        var c = await SignedInAsync(f);
        Assert.Contains("192.168.1.20", (await PageAsync(c, "/money")).QuerySelector("#money-paired")!.TextContent);

        var supervisor = await SignedInAsync(f, "supervisor");
        Assert.Empty((await PageAsync(supervisor, "/money")).QuerySelectorAll("#money-servers form"));

        using var r = await PostAsync(c, "/money", "RemoveServer", new() { ["Server"] = id.ToString() });

        Assert.Empty(f.Grid.CurrentSettings().Money.PairedServers ?? []);
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-pairing-remove" && a.GetProperty("target").GetString() == "192.168.1.20");
    }

    // ---- Gloebit (money plan 4, task 1) ----

    private const string GKey = "0b9e3c2a-1111-4c5d-8e9f-0123456789ab";
    private const string GSecret = "7d3f9a10-2222-4b6c-9d8e-fedcba987654";
    private static readonly GloebitSettings GApp = new("sandbox", GKey, GSecret, "Grid Owner", "owner@example.org");

    private static Dictionary<string, string> GloebitForm(string secret = GSecret) => new()
    {
        ["Environment"] = "sandbox", ["Key"] = GKey, ["Secret"] = secret, ["OwnerName"] = "Grid Owner", ["OwnerEmail"] = "owner@example.org",
    };

    [Fact]
    public async Task Gloebit_is_offered_when_the_gate_allows()
    {
        await using var f = Make(new FakeBooks());

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        Assert.Equal(["Off", "Play", "Gloebit"], doc.QuerySelectorAll("select[name=Mode] option").Select(o => o.GetAttribute("value")));
    }

    [Fact]
    public async Task Closed_gate_offers_no_gloebit()
    {
        await using var f = Make(new FakeBooks(), open: false);

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        Assert.DoesNotContain(doc.QuerySelectorAll("select[name=Mode] option"), o => o.GetAttribute("value") == "Gloebit");
        Assert.Null(doc.QuerySelector("#money-gloebit"));
    }

    [Fact]
    public async Task Gloebit_settings_save_and_audit_without_values()
    {
        await using var f = Make(new FakeBooks());

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Gloebit", GloebitForm());

        Assert.Equal(GApp, f.Grid.CurrentSettings().Money.Gloebit);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-gloebit");
        Assert.Equal("ok", line.GetProperty("result").GetString());
        Assert.DoesNotContain(GKey, line.GetRawText());
        Assert.DoesNotContain(GSecret, line.GetRawText());
    }

    [Fact]
    public async Task Saving_the_app_says_restart_the_world()
    {
        await using var f = Make(new FakeBooks());

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Gloebit", GloebitForm());

        Assert.Contains("Saved this computer's Gloebit app. " + MoneyPageRules.RestartWorld, MainText(await ParseAsync(r)));
    }

    [Fact]
    public async Task A_new_key_needs_its_own_secret()
    {
        // An empty secret keeps the saved one only for the same app (review, money plan 4).
        await using var f = Make(new FakeBooks(), money: m => m with { Gloebit = GApp });
        var form = GloebitForm(secret: "");
        form["Key"] = "1c2d3e4f-5555-4a6b-8c7d-0123456789ff";

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Gloebit", form);

        Assert.Equal(GApp, f.Grid.CurrentSettings().Money.Gloebit);
        Assert.Contains("Type the secret that goes with the new key.", (await ParseAsync(r)).QuerySelector(".alert-danger")!.TextContent);
    }

    [Fact]
    public async Task Supervisor_cannot_save_a_gloebit_app()
    {
        await using var f = Make(new FakeBooks());

        using var r = await PostAsync(await SignedInAsync(f, "supervisor"), "/money", "Gloebit", GloebitForm());

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
    }

    // Gloebit is real dollars: only the owner turns it on or sets its app, as with real coins (§9.1, §9.2).

    [Theory]
    // from, to, owner only
    [InlineData(MoneyMode.Off, MoneyMode.Off, false)]
    [InlineData(MoneyMode.Off, MoneyMode.Play, false)]
    [InlineData(MoneyMode.Off, MoneyMode.Real, true)]
    [InlineData(MoneyMode.Off, MoneyMode.Gloebit, true)]
    [InlineData(MoneyMode.Play, MoneyMode.Off, false)]
    [InlineData(MoneyMode.Play, MoneyMode.Play, false)]
    [InlineData(MoneyMode.Play, MoneyMode.Real, true)]
    [InlineData(MoneyMode.Play, MoneyMode.Gloebit, true)]
    [InlineData(MoneyMode.Real, MoneyMode.Off, false)]
    [InlineData(MoneyMode.Real, MoneyMode.Play, false)]
    [InlineData(MoneyMode.Real, MoneyMode.Real, false)]
    [InlineData(MoneyMode.Real, MoneyMode.Gloebit, true)]
    [InlineData(MoneyMode.Gloebit, MoneyMode.Off, false)]
    [InlineData(MoneyMode.Gloebit, MoneyMode.Play, false)]
    [InlineData(MoneyMode.Gloebit, MoneyMode.Real, true)]
    [InlineData(MoneyMode.Gloebit, MoneyMode.Gloebit, false)]
    public void Only_the_owner_turns_on_real_dollars(MoneyMode from, MoneyMode to, bool ownerOnly) =>
        Assert.Equal(ownerOnly, MoneyPageRules.OwnerOnlySwitch(from, to));

    [Fact]
    public async Task Admin_cannot_save_a_gloebit_app()
    {
        await using var f = Make(new FakeBooks());

        using var r = await PostAsync(await SignedInAsync(f, "admin"), "/money", "Gloebit", GloebitForm());

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-gloebit");
        Assert.Equal("refused: the owner's", line.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData(MoneyMode.Play)]
    [InlineData(MoneyMode.Off)]
    public async Task Admin_cannot_turn_on_gloebit(MoneyMode from)
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = from, Gloebit = GApp });
        var form = Settings(mode: "Gloebit");
        form["ConfirmSwitch"] = "true";

        using var r = await PostAsync(await SignedInAsync(f, "admin"), "/money", "Settings", form);

        Assert.Equal(from, f.Grid.CurrentSettings().Money.Mode);
        var doc = await ParseAsync(r);
        Assert.Contains("Only the owner can turn on Gloebit.", doc.QuerySelector(".alert-danger")!.TextContent);
        // The form comes back on the saved mode, which the admin can pick, not on the refused one.
        Assert.Equal(from.ToString(), doc.QuerySelector("#money-mode option[selected]")?.GetAttribute("value"));
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-settings");
        Assert.Equal("refused: mode Gloebit is the owner's", line.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Admin_without_an_app_is_told_it_is_the_owners_not_to_enter_one()
    {
        await using var f = Make(new FakeBooks());

        using var r = await PostAsync(await SignedInAsync(f, "admin"), "/money", "Settings", Settings(mode: "Gloebit"));

        Assert.Equal("Only the owner can turn on Gloebit.", (await ParseAsync(r)).QuerySelector(".alert-danger")!.TextContent.Trim());
    }

    [Fact]
    public async Task Admin_can_leave_gloebit_for_coins_with_the_tick()
    {
        // Leaving Gloebit puts no dollars at risk; the tick still says coin balances come back.
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Gloebit, Gloebit = GApp });
        var form = Settings(mode: "Play");
        form["ConfirmSwitch"] = "true";

        using var r = await PostAsync(await SignedInAsync(f, "admin"), "/money", "Settings", form);

        Assert.Equal(MoneyMode.Play, f.Grid.CurrentSettings().Money.Mode);
    }

    [Fact]
    public async Task Admin_can_turn_coins_back_on_after_an_emergency_off()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Off, Gloebit = GApp });
        var c = await SignedInAsync(f, "admin");

        // A Gloebit app was saved once, so the switch asks for the tick, and the admin has the box to tick.
        Assert.NotNull((await PageAsync(c, "/money")).QuerySelector("#money-confirm-switch"));
        var form = Settings(mode: "Play");
        form["ConfirmSwitch"] = "true";
        using var r = await PostAsync(c, "/money", "Settings", form);

        Assert.Equal(MoneyMode.Play, f.Grid.CurrentSettings().Money.Mode);
    }

    [Fact]
    public async Task Admin_can_turn_gloebit_off()
    {
        // Turning money off stops payments: any admin may, as an emergency step.
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Gloebit, Gloebit = GApp });

        using var r = await PostAsync(await SignedInAsync(f, "admin"), "/money", "Settings", Settings(mode: "Off"));

        Assert.Equal(MoneyMode.Off, f.Grid.CurrentSettings().Money.Mode);
    }

    [Fact]
    public async Task Admin_can_change_other_settings_while_gloebit_runs()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Gloebit, Gloebit = GApp });
        var form = Settings(mode: "Gloebit");
        form["UploadFee"] = "15";

        using var r = await PostAsync(await SignedInAsync(f, "admin"), "/money", "Settings", form);

        Assert.Null((await ParseAsync(r)).QuerySelector(".alert-danger"));
        Assert.Equal(15L, f.Grid.CurrentSettings().Money.UploadFee);
        Assert.Equal(MoneyMode.Gloebit, f.Grid.CurrentSettings().Money.Mode);
    }

    [Fact]
    public async Task Admin_sees_gloebit_locked_and_no_app_form()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Gloebit = GApp });

        var doc = await PageAsync(await SignedInAsync(f, "admin"), "/money");

        Assert.True(doc.QuerySelector("#money-mode option[value=Gloebit]")!.HasAttribute("disabled"));
        Assert.False(doc.QuerySelector("#money-mode option[value=Play]")!.HasAttribute("disabled"));
        Assert.Null(doc.QuerySelector("form[action*='handler=Gloebit']"));
        Assert.Contains("Only the owner can turn on Gloebit or change its Gloebit app.", MainText(doc));
    }

    [Fact]
    public async Task Admin_in_gloebit_mode_may_pick_any_mode()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Gloebit, Gloebit = GApp });

        var doc = await PageAsync(await SignedInAsync(f, "admin"), "/money");

        Assert.All(doc.QuerySelectorAll("#money-mode option"), o => Assert.False(o.HasAttribute("disabled")));
    }

    [Fact]
    public async Task Owner_sees_gloebit_open()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Gloebit = GApp });

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        Assert.False(doc.QuerySelector("#money-mode option[value=Gloebit]")!.HasAttribute("disabled"));
        Assert.NotNull(doc.QuerySelector("form[action*='handler=Gloebit']"));
        Assert.NotNull(doc.QuerySelector("#money-confirm-switch"));
    }

    [Fact]
    public async Task Secret_is_never_shown_back()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Gloebit = GApp });

        var html = (await PageAsync(await SignedInAsync(f), "/money")).DocumentElement.OuterHtml;

        Assert.DoesNotContain(GSecret, html);
        Assert.DoesNotContain(GKey, html);
        Assert.Contains("••••89ab", html);
    }

    [Fact]
    public async Task Empty_secret_keeps_the_stored_one()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Gloebit = GApp });

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Gloebit", GloebitForm(secret: ""));

        Assert.Equal(GSecret, f.Grid.CurrentSettings().Money.Gloebit!.Secret);
    }

    [Fact]
    public async Task Bad_gloebit_values_are_refused()
    {
        await using var f = Make(new FakeBooks());
        var form = GloebitForm();
        form["Key"] = "not-a-key";

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Gloebit", form);

        Assert.NotNull((await ParseAsync(r)).QuerySelector(".alert-danger"));
        Assert.Null(f.Grid.CurrentSettings().Money.Gloebit);
    }

    [Fact]
    public async Task Gloebit_mode_needs_a_complete_app()
    {
        await using var f = Make(new FakeBooks());

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Settings", Settings(mode: "Gloebit"));

        Assert.NotNull((await ParseAsync(r)).QuerySelector(".alert-danger"));
        Assert.Equal(MoneyMode.Play, f.Grid.CurrentSettings().Money.Mode);
    }

    [Fact]
    public async Task Switching_coins_and_gloebit_needs_the_tick()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Gloebit = GApp });
        var c = await SignedInAsync(f);

        using (var refused = await PostAsync(c, "/money", "Settings", Settings(mode: "Gloebit")))
        {
            var doc = await ParseAsync(refused);
            Assert.Contains("GridGate coin balances don't move to Gloebit", doc.QuerySelector(".alert-danger")!.TextContent);
            Assert.Equal(MoneyMode.Play, f.Grid.CurrentSettings().Money.Mode);
            // The form keeps what the owner chose, so ticking the box is all that's left to do (live check, plan 4).
            Assert.Equal("Gloebit", doc.QuerySelector("#money-mode option[selected]")?.GetAttribute("value"));
        }

        var ticked = Settings(mode: "Gloebit");
        ticked["ConfirmSwitch"] = "true";
        using var ok = await PostAsync(c, "/money", "Settings", ticked);

        Assert.Equal(MoneyMode.Gloebit, f.Grid.CurrentSettings().Money.Mode);
    }

    [Fact]
    public async Task Going_to_gloebit_through_off_still_needs_the_tick()
    {
        // Money review 2026-11-15, H-5: Play, then Off, then Gloebit never asked.
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Off, Gloebit = GApp });
        f.Grid.UpdateSettings(s => s with { Db = s.Db with { MoneyPassword = "made-when-play-ran" } }, writeConfig: false);
        var c = await SignedInAsync(f);

        using var refused = await PostAsync(c, "/money", "Settings", Settings(mode: "Gloebit"));

        Assert.Contains("GridGate coin balances don't move to Gloebit", (await ParseAsync(refused)).QuerySelector(".alert-danger")!.TextContent);
        Assert.Equal(MoneyMode.Off, f.Grid.CurrentSettings().Money.Mode);
    }

    [Theory]
    // from, to, coins ran here before, a Gloebit app was saved, asks
    [InlineData(MoneyMode.Play, MoneyMode.Gloebit, true, true, true)]
    [InlineData(MoneyMode.Gloebit, MoneyMode.Play, true, true, true)]
    [InlineData(MoneyMode.Off, MoneyMode.Gloebit, true, true, true)]
    [InlineData(MoneyMode.Off, MoneyMode.Gloebit, false, true, false)]
    [InlineData(MoneyMode.Off, MoneyMode.Play, true, true, true)]
    [InlineData(MoneyMode.Off, MoneyMode.Play, true, false, false)]
    [InlineData(MoneyMode.Off, MoneyMode.Real, false, true, true)]
    [InlineData(MoneyMode.Play, MoneyMode.Real, true, true, false)]
    [InlineData(MoneyMode.Play, MoneyMode.Off, true, true, false)]
    [InlineData(MoneyMode.Gloebit, MoneyMode.Off, true, true, false)]
    [InlineData(MoneyMode.Gloebit, MoneyMode.Gloebit, true, true, false)]
    public void Which_switches_cross_coins_and_gloebit(MoneyMode from, MoneyMode to, bool coinsUsed, bool gloebitUsed, bool asks) =>
        Assert.Equal(asks, MoneyPageRules.CrossesGloebit(from, to, coinsUsed, gloebitUsed));

    [Fact]
    public async Task Mode_change_says_restart_the_world()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Off });

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Settings", Settings(mode: "Play"));

        Assert.Contains("Takes effect after you restart GridGate and then the world (Power → Restart on the dashboard).",
            MainText(await ParseAsync(r)));
    }

    [Fact]
    public async Task Network_only_gloebit_warns_about_reachability()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Mode = MoneyMode.Gloebit, Gloebit = GApp });

        var text = MainText(await PageAsync(await SignedInAsync(f), "/money"));

        Assert.Contains("Gloebit handles payments on this grid.", text);
        Assert.Contains("Gloebit needs your regions reachable from the internet: purchases and payments call back to them.", text);
    }

    [Fact]
    public async Task Person_page_shows_balance_and_history()
    {
        var books = new FakeBooks { Balance = 1234 };
        books.History.Add(new HistoryLine(9, DateTimeOffset.UtcNow, EntryType.StaffGift, null, 25, 1234, null, "welcome gift"));
        await using var f = Make(books);

        var text = MainText(await PageAsync(await SignedInAsync(f), $"/money/person?id={Ann}"));

        Assert.Contains("Ann Smith", text);
        Assert.Contains("1,234", text);
        Assert.Contains("welcome gift", text);
        Assert.Contains($"history {Ann} 100", books.Calls);
    }

    [Fact]
    public async Task Staff_payout_switch_is_audited()
    {
        var books = new FakeBooks();
        await using var f = Make(books);

        using var r = await PostAsync(await SignedInAsync(f), $"/money/person?id={Ann}", "Payouts", new() { ["On"] = "true" });

        Assert.Contains($"payouts {Ann} True", books.Calls);
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-payouts-staff" && a.GetProperty("result").GetString() == "on");
    }

    [Fact]
    public async Task Account_freeze_is_audited()
    {
        var books = new FakeBooks();
        await using var f = Make(books);
        var c = await SignedInAsync(f);

        using var r = await PostAsync(c, $"/money/person?id={Ann}", "FreezeAccount", []);
        using var u = await PostAsync(c, $"/money/person?id={Ann}", "UnfreezeAccount", []);

        Assert.Contains($"freeze-account {Ann} True", books.Calls);
        var actions = Audit(f).Select(a => a.GetProperty("action").GetString()).ToList();
        Assert.Contains("money-freeze-account", actions);
        Assert.Contains("money-unfreeze-account", actions);
    }

    [Fact]
    public async Task Unknown_person_is_404()
    {
        await using var f = Make(new FakeBooks());

        using var r = await (await SignedInAsync(f)).GetAsync($"/money/person?id={Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Database_down_says_so()
    {
        await using var f = Make(new FakeBooks { Down = true });

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        Assert.Contains("The money database can't be reached", MainText(doc));
    }

    [Fact]
    public async Task Dashboard_shows_frozen_notice()
    {
        await using var f = Make(new FakeBooks { Frozen = (true, "test") });
        f.Services.GetRequiredService<MoneyStatus>().ServiceRunning = true;

        var doc = await PageAsync(await SignedInAsync(f), "/dashboard");

        var notice = doc.QuerySelector("#money-frozen");
        Assert.NotNull(notice);
        Assert.Contains("alert-danger", notice.ClassName);
        Assert.Contains("All payments are frozen: test. Open Money to unfreeze.", notice.TextContent);
    }

    [Fact]
    public async Task Warning_alerts_show_on_the_dashboard_with_a_money_link()
    {
        var region = Guid.Parse("e0000000-0000-0000-0000-0000000000e1");
        var alerts = new FakeAlerts();
        var at = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        // A warning from the key's count across its regions: this row has seen one claim only.
        alerts.Open.Add(new MoneyAlert(7, AlertKind.UnhandedArrival, AlertLevel.Warning, "p1", region, Ann, 1, at, at));
        alerts.Open.Add(new MoneyAlert(8, AlertKind.KeyLimitReached, AlertLevel.Warning, "p1", null, null, 1, at, at));
        alerts.Open.Add(new MoneyAlert(9, AlertKind.RegionMoved, AlertLevel.Notice, "p1", region, null, 1, at, at));
        alerts.Open.Add(new MoneyAlert(10, AlertKind.UnhandedArrival, AlertLevel.Warning, "p2", region, Ann, 1, at, at, OtherKeyId: "p1"));
        await using var f = Make(new FakeBooks(), alerts: alerts);
        f.Services.GetRequiredService<MoneyStatus>().ServiceRunning = true;

        var doc = await PageAsync(await SignedInAsync(f), "/dashboard");

        var conflict = doc.QuerySelector("#money-alert-10");
        Assert.NotNull(conflict);
        Assert.Contains("the region program p2 and the region program p1 disagree about where Ann Smith is", conflict.TextContent);

        var arrival = doc.QuerySelector("#money-alert-7");
        Assert.NotNull(arrival);
        Assert.Contains("alert-danger", arrival.ClassName);
        Assert.Contains("wasn't handed", arrival.TextContent);
        Assert.DoesNotContain("once", arrival.TextContent); // a warning's row count would understate it
        Assert.NotNull(arrival.QuerySelector("a[href='/money']"));
        var limit = doc.QuerySelector("#money-alert-8");
        Assert.NotNull(limit);
        Assert.Contains("reached its daily money limit", limit.TextContent);
        Assert.NotNull(limit.QuerySelector("a[href='/money']"));
        Assert.Null(doc.QuerySelector("#money-alert-9")); // notices are for the Money page
    }

    /// <summary>
    /// Robust's connection breaks while the dashboard names who a disagreement is about: the person is named by id, as for
    /// any other Robust failure, and the dashboard still shows.
    /// </summary>
    [Fact]
    public async Task A_broken_Robust_connection_names_the_person_by_id_on_the_dashboard()
    {
        var region = Guid.Parse("e0000000-0000-0000-0000-0000000000e1");
        var alerts = new FakeAlerts();
        var at = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        alerts.Open.Add(new MoneyAlert(10, AlertKind.UnhandedArrival, AlertLevel.Warning, "p2", region, Ann, 1, at, at, OtherKeyId: "p1"));
        await using var f = Make(new FakeBooks(), alerts: alerts);
        f.Services.GetRequiredService<MoneyStatus>().ServiceRunning = true;
        var c = await SignedInAsync(f);
        f.Robust.OnFindAccountById = () => throw new IOException("The connection was reset.");

        var doc = await PageAsync(c, "/dashboard");

        Assert.Contains($"disagree about where avatar {Ann} is", doc.QuerySelector("#money-alert-10")!.TextContent);
    }

    [Fact]
    public async Task Dashboard_shows_service_failure()
    {
        await using var f = Make(new FakeBooks());
        f.Services.GetRequiredService<MoneyStatus>().ServiceRunning = false;
        f.Services.GetRequiredService<MoneyStatus>().ServiceError = "IOException";

        var doc = await PageAsync(await SignedInAsync(f), "/dashboard");

        Assert.Contains("The money service couldn't start, so payments are refused until GridGate restarts. The launcher log has the details.",
            doc.QuerySelector("#money-service")!.TextContent);
    }

    [Fact]
    public async Task Dashboard_says_money_starts_on_restart()
    {
        // Live check, plan 3: Play coins chosen while GridGate runs. The service and the money database start with GridGate,
        // so until then the dashboard says that, calmly, and doesn't ask a database that doesn't exist yet.
        var books = new FakeBooks { Down = true };
        await using var f = Make(books);
        var status = f.Services.GetRequiredService<MoneyStatus>();
        status.ServiceRunning = false;
        status.ServiceError = null;

        var doc = await PageAsync(await SignedInAsync(f), "/dashboard");

        var notice = doc.QuerySelector("#money-restart");
        Assert.NotNull(notice);
        Assert.Equal("Money was turned on after GridGate started. Restart GridGate and then the world (Power → Restart) for money to start.", notice!.TextContent.Trim());
        Assert.DoesNotContain("alert-danger", notice.ClassName);
        Assert.Null(doc.QuerySelector("#money-database"));
        Assert.Empty(books.Calls);
    }

    [Fact]
    public async Task Dashboard_shows_database_down()
    {
        await using var f = Make(new FakeBooks { Down = true });
        f.Services.GetRequiredService<MoneyStatus>().ServiceRunning = true;

        var doc = await PageAsync(await SignedInAsync(f), "/dashboard");

        Assert.Contains("The money database can't be reached; payments are refused.", doc.QuerySelector("#money-database")!.TextContent);
    }

    [Fact]
    public async Task Dashboard_quiet_when_off()
    {
        var books = new FakeBooks { Frozen = (true, "test") };
        await using var f = Make(books, open: false);
        f.Services.GetRequiredService<MoneyStatus>().ServiceError = "IOException";

        var doc = await PageAsync(await SignedInAsync(f), "/dashboard");

        Assert.Null(doc.QuerySelector("#money-frozen"));
        Assert.Null(doc.QuerySelector("#money-service"));
        Assert.Empty(books.Calls);
    }

    [Fact]
    public async Task Person_page_links_to_coins_when_on()
    {
        await using var f = Make(new FakeBooks());
        var c = await SignedInAsync(f);

        var on = await PageAsync(c, $"/people/{Ann}");

        Assert.NotNull(on.QuerySelector($"a[href='/money/person?id={Ann}']"));
    }

    // ---- verified regions, visitors, limits and alerts ----

    private static readonly Guid Sam = Guid.Parse("d0000000-0000-0000-0000-0000000000d5");
    private static readonly Guid Jo = Guid.Parse("d0000000-0000-0000-0000-0000000000d6");
    private static readonly Guid Welcome = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid NeonPier = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid Market = Guid.Parse("e0000000-0000-0000-0000-000000000003");
    private static readonly Guid OldDocks = Guid.Parse("e0000000-0000-0000-0000-000000000004");
    private static readonly Guid Harbour = Guid.Parse("e0000000-0000-0000-0000-000000000005");
    private static readonly Guid Quay = Guid.Parse("e0000000-0000-0000-0000-000000000006");
    private static readonly Guid Pond = Guid.Parse("e0000000-0000-0000-0000-000000000007");
    private static readonly Guid[] AllRegions = [Welcome, NeonPier, Market, OldDocks, Harbour, Quay, Pond];

    private static string KeyOf(Guid server) => "server:" + server.ToString("N");

    /// <summary>
    /// A Grid Asset Server running Welcome and Pond (program p1), with Sam's server (10.0.0.5) and Jo's (10.0.0.6) paired.
    /// Market Square is pinned to Jo's server but now registers from Sam's; Old Docks is at an address nobody paired.
    /// </summary>
    private static LauncherFactory MakeGrid(FakeBooks? books = null, FakeAlerts? alerts = null)
    {
        var f = Make(books ?? new FakeBooks(), plan: OwnerPlan.GridAsset, alerts: alerts,
            money: m => m with
            {
                PairedServers =
                [
                    new PairedServer(Sam, "10.0.0.5", new string('A', 64), DateTimeOffset.UtcNow),
                    new PairedServer(Jo, "10.0.0.6", new string('B', 64), DateTimeOffset.UtcNow),
                ],
            },
            more: s => s with { Programs = [new ProgramSettings("p1", 9000, 9001)], Regions = [new RegionSettings("p1", "Welcome", Welcome), new RegionSettings("p1", "Pond", Pond)] });
        f.MoneyRegions.States.AddRange(
        [
            new RegionState(Welcome, "Welcome", "127.0.0.1", 9000, "p1", RegionStatus.Verified),
            new RegionState(NeonPier, "Neon Pier", "10.0.0.5", 9000, KeyOf(Sam), RegionStatus.Verified),
            new RegionState(Market, "Market Square", "10.0.0.5", 9001, KeyOf(Sam), RegionStatus.Moved, KeyOf(Jo)),
            new RegionState(OldDocks, "Old Docks", "10.0.0.9", 9000, null, RegionStatus.NotPaired),
            new RegionState(Harbour, "Harbour", "10.0.0.6", 9000, KeyOf(Jo), RegionStatus.NeedsUpdate),
            new RegionState(Quay, "Quay", "10.0.0.6", 9001, KeyOf(Jo), RegionStatus.NotHeardFrom),
            new RegionState(Pond, "Pond", "127.0.0.1", 9002, "p1", RegionStatus.NoGridGateMoney),
        ]);
        f.MoneyRegions.Keys[KeyOf(Sam)] = new KeyHeard(2, DateTime.UtcNow, RegionStatus.Verified);
        f.MoneyRegions.Keys[KeyOf(Jo)] = new KeyHeard(1, DateTime.UtcNow, RegionStatus.NeedsUpdate);
        return f;
    }

    private static string? ErrorText(IHtmlDocument d) => d.QuerySelector(".alert-danger")?.TextContent;

    private static List<string> Results(LauncherFactory f, string action) =>
        Audit(f).Where(a => a.GetProperty("action").GetString() == action).Select(a => a.GetProperty("result").GetString()!).ToList();

    private static List<string> Targets(LauncherFactory f, string action) =>
        Audit(f).Where(a => a.GetProperty("action").GetString() == action).Select(a => a.GetProperty("target").GetString()!).ToList();

    [Fact]
    public async Task Regions_section_lists_every_region_with_its_status()
    {
        await using var f = MakeGrid();

        var doc = await PageAsync(await SignedInAsync(f, "supervisor"), "/money");

        string Row(Guid id) => doc.QuerySelector($"#money-region-{id}")!.TextContent;
        Assert.Contains("Welcome", Row(Welcome));
        Assert.Contains("This computer", Row(Welcome));
        Assert.Contains("Verified", Row(Welcome));
        Assert.Contains("Region Server at 10.0.0.5", Row(NeonPier));
        Assert.Contains("Verified", Row(NeonPier));
        Assert.Contains("Moved here from Region Server at 10.0.0.6, waiting for you", Row(Market));
        Assert.Contains("10.0.0.9", Row(OldDocks));
        Assert.Contains("Not paired", Row(OldDocks));
        Assert.Contains("Needs an update", Row(Harbour));
        Assert.Contains("Not heard from", Row(Quay));
        Assert.Contains("No GridGate money", Row(Pond));
        // Every status says what it means, and every reason money is off links to the owner's guide.
        foreach (var id in AllRegions)
        {
            var row = doc.QuerySelector($"#money-region-{id}")!;
            Assert.NotNull(row.QuerySelector(".help-tip"));
            if (id != Welcome && id != NeonPier) Assert.NotNull(row.QuerySelector("a[href*='money-verification.md#']"));
        }
        Assert.NotNull(doc.QuerySelector("#money-regions-title .help-tip"));

        // Region Servers: each one's status and the money protocol its module speaks.
        Assert.Contains("Verified", doc.QuerySelector($"#money-server-{Sam}")!.TextContent);
        Assert.Contains("money protocol 2", doc.QuerySelector($"#money-server-{Sam}")!.TextContent);
        Assert.Contains("Needs an update", doc.QuerySelector($"#money-server-{Jo}")!.TextContent);
        Assert.Contains("money protocol 1", doc.QuerySelector($"#money-server-{Jo}")!.TextContent);
    }

    [Fact]
    public async Task Moved_region_shows_approve_and_keep_off_to_the_owner_only()
    {
        var alerts = new FakeAlerts();
        var at = DateTime.UtcNow;
        alerts.Open.Add(new MoneyAlert(4, AlertKind.RegionMoved, AlertLevel.Notice, KeyOf(Sam), Market, null, 1, at, at));
        await using var f = MakeGrid(alerts: alerts);
        var owner = await SignedInAsync(f);

        var row = (await PageAsync(owner, "/money")).QuerySelector($"#money-region-{Market}")!;
        var approve = row.QuerySelector("form[action*='handler=ApproveMove']");
        Assert.NotNull(approve);
        Assert.Equal(Market.ToString(), approve.QuerySelector("input[name=Region]")?.GetAttribute("value"));
        Assert.Equal(Sam.ToString(), approve.QuerySelector("input[name=Server]")?.GetAttribute("value"));
        Assert.NotNull(row.QuerySelector("form[action*='handler=KeepOff']"));

        foreach (var role in new[] { "admin", "supervisor" })
        {
            var c = await SignedInAsync(f, role);
            Assert.Empty((await PageAsync(c, "/money")).QuerySelector($"#money-region-{Market}")!.QuerySelectorAll("form"));
            using var a = await PostAsync(c, "/money", "ApproveMove", new() { ["Region"] = Market.ToString(), ["Server"] = Sam.ToString() });
            using var k = await PostAsync(c, "/money", "KeepOff", new() { ["Region"] = Market.ToString() });
            Assert.Equal(HttpStatusCode.Forbidden, a.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, k.StatusCode);
        }
        Assert.DoesNotContain(f.MoneyRegions.Calls, x => x.StartsWith("approve"));
        Assert.Single(alerts.Open);

        // Keeping it off changes no pin: it only clears the notice and is audited.
        using (var kept = await PostAsync(owner, "/money", "KeepOff", new() { ["Region"] = Market.ToString() }))
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.DoesNotContain(f.MoneyRegions.Calls, x => x.StartsWith("approve"));
        Assert.Contains("clear 4 Test Owner", alerts.Calls);

        // A region that isn't waiting for a move can't be approved.
        using (var stale = await PostAsync(owner, "/money", "ApproveMove", new() { ["Region"] = Welcome.ToString(), ["Server"] = Sam.ToString() }))
            Assert.NotNull(ErrorText(await ParseAsync(stale)));
        Assert.DoesNotContain(f.MoneyRegions.Calls, x => x.StartsWith("approve"));

        using var ok = await PostAsync(owner, "/money", "ApproveMove", new() { ["Region"] = Market.ToString(), ["Server"] = Sam.ToString() });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains($"approve {Market} {Sam}", f.MoneyRegions.Calls);
        Assert.Equal(["kept off", "approved"], Results(f, "money-region-move"));
        Assert.All(Targets(f, "money-region-move"), t => Assert.Equal("Market Square", t));
    }

    private static readonly Guid Cove = Guid.Parse("e0000000-0000-0000-0000-000000000011");
    private static readonly Guid Reef = Guid.Parse("e0000000-0000-0000-0000-000000000012");
    private static readonly Guid Lagoon = Guid.Parse("e0000000-0000-0000-0000-000000000013");

    private static LauncherFactory MakeGridWithWaiting()
    {
        var f = MakeGrid();
        f.MoneyRegions.States.AddRange(
        [
            new RegionState(Cove, "Cove", "10.0.0.5", 9002, KeyOf(Sam), RegionStatus.Waiting),
            new RegionState(Reef, "Reef", "10.0.0.5", 9003, KeyOf(Sam), RegionStatus.Waiting),
            new RegionState(Lagoon, "Lagoon", "10.0.0.6", 9002, KeyOf(Jo), RegionStatus.Waiting),
        ]);
        return f;
    }

    [Fact]
    public async Task Waiting_regions_show_a_confirm_button_to_the_owner_only()
    {
        await using var f = MakeGridWithWaiting();

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        var sam = doc.QuerySelector($"#money-server-{Sam} form[action*='handler=ConfirmRegions']")!;
        Assert.Equal(Sam.ToString(), sam.QuerySelector("input[name=Server]")?.GetAttribute("value"));
        Assert.Equal("Confirm its 2 regions", sam.QuerySelector("button")!.TextContent.Trim());
        Assert.Equal("Confirm its region", doc.QuerySelector($"#money-server-{Jo} form[action*='handler=ConfirmRegions'] button")!.TextContent.Trim());
        // A server with nothing waiting has no button.
        await using var clear = MakeGrid();
        Assert.Null((await PageAsync(await SignedInAsync(clear), "/money")).QuerySelector("form[action*='handler=ConfirmRegions']"));
        // The region rows say what they wait for, and offer no keep-off.
        var row = doc.QuerySelector($"#money-region-{Cove}")!;
        Assert.Contains("Waiting for you to confirm: listed at Region Server at 10.0.0.5 when it paired", row.TextContent);
        Assert.Empty(row.QuerySelectorAll("form"));

        foreach (var role in new[] { "admin", "supervisor" })
            Assert.Empty((await PageAsync(await SignedInAsync(f, role), "/money")).QuerySelectorAll("form[action*='handler=ConfirmRegions']"));
    }

    [Fact]
    public async Task Confirming_one_region_is_audited_as_one_region()
    {
        await using var f = MakeGridWithWaiting();

        using var ok = await PostAsync(await SignedInAsync(f), "/money", "ConfirmRegions", new() { ["Server"] = Jo.ToString() });

        Assert.Equal(["1 region"], Results(f, "money-region-confirm"));
    }

    [Fact]
    public async Task Confirming_regions_is_audited_and_refused_to_admins()
    {
        await using var f = MakeGridWithWaiting();
        foreach (var role in new[] { "admin", "supervisor" })
        {
            using var denied = await PostAsync(await SignedInAsync(f, role), "/money", "ConfirmRegions", new() { ["Server"] = Sam.ToString() });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        Assert.DoesNotContain(f.MoneyRegions.Calls, x => x.StartsWith("confirm"));
        var owner = await SignedInAsync(f);

        // A server that isn't paired, or has nothing waiting, confirms nothing.
        using (var gone = await PostAsync(owner, "/money", "ConfirmRegions", new() { ["Server"] = Guid.NewGuid().ToString() }))
            Assert.NotNull(ErrorText(await ParseAsync(gone)));
        f.MoneyRegions.States.RemoveAll(r => r.Status == RegionStatus.Waiting && r.KeyId == KeyOf(Jo));
        using (var none = await PostAsync(owner, "/money", "ConfirmRegions", new() { ["Server"] = Jo.ToString() }))
            Assert.NotNull(ErrorText(await ParseAsync(none)));
        Assert.DoesNotContain(f.MoneyRegions.Calls, x => x.StartsWith("confirm"));

        using var ok = await PostAsync(owner, "/money", "ConfirmRegions", new() { ["Server"] = Sam.ToString() });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains($"confirm {Sam}", f.MoneyRegions.Calls);
        Assert.Equal(["2 regions"], Results(f, "money-region-confirm"));
        Assert.Equal(["10.0.0.5"], Targets(f, "money-region-confirm"));
    }

    [Fact]
    public async Task Removing_a_server_no_longer_touches_the_money_database()
    {
        await using var f = MakeGrid();
        f.MoneyRegions.Down = true; // every money call fails; the page's own region list reads fail too, so removal must not need them
        var owner = await SignedInAsync(f);
        f.MoneyRegions.Calls.Clear();

        using var r = await PostAsync(owner, "/money", "RemoveServer", new() { ["Server"] = Sam.ToString() });

        Assert.DoesNotContain(f.Grid.CurrentSettings().Money.PairedServers!, p => p.Id == Sam);
        Assert.DoesNotContain("pins were kept", await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-pairing-remove");
    }

    [Fact]
    public async Task Admin_cannot_set_key_or_person_limits()
    {
        var books = new FakeBooks();
        await using var f = MakeGrid(books);
        var admin = await SignedInAsync(f, "admin");
        Assert.Empty((await PageAsync(admin, "/money")).QuerySelectorAll("form[action*='handler=KeyLimits']"));
        Assert.Empty((await PageAsync(admin, $"/money/person?id={Ann}")).QuerySelectorAll("form[action*='handler=Limits']"));

        using var k = await PostAsync(admin, "/money", "KeyLimits", new() { ["KeyId"] = KeyOf(Sam), ["DailySpend"] = "5", ["Reason"] = "x" });
        using var p = await PostAsync(admin, $"/money/person?id={Ann}", "Limits", new() { ["DailySpend"] = "5", ["Reason"] = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, k.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, p.StatusCode);
        Assert.DoesNotContain(books.Calls, x => x.StartsWith("set-"));
    }

    [Fact]
    public async Task Supervisor_sees_limits_but_no_forms()
    {
        var books = new FakeBooks { Limits = new LimitView(250_000, 20_000, true, false, null, 1234, 0) };
        await using var f = MakeGrid(books);
        var c = await SignedInAsync(f, "supervisor");

        var doc = await PageAsync(c, "/money");

        var limits = doc.QuerySelector("#money-limits")!;
        Assert.Contains("10,000", limits.TextContent);
        Assert.Contains("100,000", limits.TextContent);
        Assert.Contains("20,000", limits.TextContent);
        var sam = limits.QuerySelector($"tr[data-key='{KeyOf(Sam)}']")!;
        Assert.Contains("Region Server at 10.0.0.5", sam.TextContent);
        Assert.Contains("250,000", sam.TextContent);
        Assert.Contains("own limit", sam.TextContent);
        Assert.Contains("1,234", sam.TextContent);
        Assert.NotNull(limits.QuerySelector("tr[data-key='p1']"));
        Assert.Empty(limits.QuerySelectorAll("form"));

        var person = await PageAsync(c, $"/money/person?id={Ann}");
        var line = person.QuerySelector("#person-limits")!.TextContent;
        Assert.Contains("Daily spending: 1,234 of 250,000 used (own limit).", line);
        Assert.Contains("Daily script payouts: 0 of 20,000 used (grid default).", line);
        Assert.Empty(person.QuerySelectorAll("form[action*='handler=Limits']"));
    }

    [Fact]
    public async Task Owner_sets_a_person_limit_higher_and_lower_with_reason_and_audit()
    {
        var books = new FakeBooks();
        await using var f = Make(books);
        var c = await SignedInAsync(f);
        var page = $"/money/person?id={Ann}";
        Assert.NotNull((await PageAsync(c, page)).QuerySelector("form[action*='handler=Limits']"));

        using (var none = await PostAsync(c, page, "Limits", new() { ["DailySpend"] = "50000", ["Reason"] = " " }))
            Assert.Contains("Say why", ErrorText(await ParseAsync(none)));
        using (var big = await PostAsync(c, page, "Limits", new() { ["DailySpend"] = "100000001", ["Reason"] = "x" }))
            Assert.Contains("0 to 100,000,000", ErrorText(await ParseAsync(big)));
        var today = f.Time.GetUtcNow().UtcDateTime.Date; // midnight UTC today has passed
        using (var old = await PostAsync(c, page, "Limits", new() { ["DailySpend"] = "500", ["Until"] = today.ToString("yyyy-MM-dd"), ["Reason"] = "x" }))
            Assert.Contains("end date", ErrorText(await ParseAsync(old)));
        Assert.DoesNotContain(books.Calls, x => x.StartsWith("set-person-limits"));

        var end = today.AddDays(7);
        using var higher = await PostAsync(c, page, "Limits", new() { ["DailySpend"] = "50000", ["DailyPayout"] = "", ["Reason"] = "land sale" });
        using var lower = await PostAsync(c, page, "Limits",
            new() { ["DailySpend"] = "500", ["DailyPayout"] = "100", ["Until"] = end.ToString("yyyy-MM-dd"), ["Reason"] = "asked for less" });
        using var back = await PostAsync(c, page, "Limits", new() { ["DailySpend"] = "", ["DailyPayout"] = "", ["Reason"] = "back to normal" });

        Assert.Equal([$"set-person-limits {Ann} 50000  ", $"set-person-limits {Ann} 500 100 {end:O}", $"set-person-limits {Ann}   "],
            books.Calls.Where(x => x.StartsWith("set-person-limits")));
        Assert.Equal([new StaffAction("Test Owner", "land sale"), new StaffAction("Test Owner", "asked for less"), new StaffAction("Test Owner", "back to normal")],
            books.Staff);
        Assert.Equal(["ok spend 50000 payout default", $"ok spend 500 payout 100 until {end:yyyy-MM-dd}", "ok default"], Results(f, "money-person-limits"));
        Assert.All(Targets(f, "money-person-limits"), t => Assert.Equal("Ann Smith", t));
    }

    [Fact]
    public async Task Owner_sets_a_server_limit_and_back_to_default()
    {
        var books = new FakeBooks();
        await using var f = MakeGrid(books);
        var c = await SignedInAsync(f);
        var row = (await PageAsync(c, "/money")).QuerySelector($"#money-limits tr[data-key='{KeyOf(Sam)}']")!;
        Assert.NotNull(row.QuerySelector("form[action*='handler=KeyLimits']"));

        using var up = await PostAsync(c, "/money", "KeyLimits", new() { ["KeyId"] = KeyOf(Sam), ["DailySpend"] = "250000", ["DailyPayout"] = "", ["Reason"] = "busy mall" });
        using var back = await PostAsync(c, "/money", "KeyLimits", new() { ["KeyId"] = KeyOf(Sam), ["DailySpend"] = "", ["DailyPayout"] = "", ["Reason"] = "back to normal" });
        using var local = await PostAsync(c, "/money", "KeyLimits", new() { ["KeyId"] = "p1", ["DailySpend"] = "", ["DailyPayout"] = "1000", ["Reason"] = "quiet" });
        using (var unknown = await PostAsync(c, "/money", "KeyLimits",
                   new() { ["KeyId"] = "server:" + Guid.NewGuid().ToString("N"), ["DailySpend"] = "5", ["Reason"] = "x" }))
            Assert.NotNull(ErrorText(await ParseAsync(unknown)));
        using (var none = await PostAsync(c, "/money", "KeyLimits", new() { ["KeyId"] = KeyOf(Sam), ["DailySpend"] = "5", ["Reason"] = "" }))
            Assert.Contains("Say why", ErrorText(await ParseAsync(none)));

        Assert.Equal([$"set-limits Key {KeyOf(Sam)} 250000  ", $"set-limits Key {KeyOf(Sam)}   ", "set-limits Key p1  1000 "],
            books.Calls.Where(x => x.StartsWith("set-limits")));
        Assert.Equal(["ok spend 250000 payout default", "ok default", "ok spend default payout 1000"], Results(f, "money-key-limits"));
        Assert.Equal(["Region Server at 10.0.0.5", "Region Server at 10.0.0.5", "region program p1"], Targets(f, "money-key-limits"));
    }

    [Fact]
    public async Task Visitor_money_defaults_to_only_grids_I_list()
    {
        await using var f = Make(new FakeBooks());
        var c = await SignedInAsync(f);

        var visitors = (await PageAsync(c, "/money")).QuerySelector("#money-visitors")!;

        Assert.Equal("Listed", visitors.QuerySelector("input[name=Visitors][checked]")?.GetAttribute("value"));
        Assert.Contains("Only grids I list", visitors.TextContent);
        Assert.Contains("People arriving from another grid straight into a Region Server's region can visit, but money is paused for that visit. " +
            "Keep your hypergrid welcome region on this computer.", visitors.TextContent);
        Assert.NotNull(visitors.QuerySelector("#money-no-grids"));
        Assert.NotNull(visitors.QuerySelector("a[href*='money-verification.md#']"));

        using (var bad = await PostAsync(c, "/money", "Visitors", new() { ["Visitors"] = "Sometimes" }))
            Assert.NotNull(ErrorText(await ParseAsync(bad)));
        Assert.Null(f.Grid.CurrentSettings().Money.Visitors);

        using var all = await PostAsync(c, "/money", "Visitors", new() { ["Visitors"] = "All" });

        Assert.Equal(VisitorMoney.All, f.Grid.CurrentSettings().Money.Visitors);
        Assert.Equal(["all"], Results(f, "money-visitors"));
        Assert.Equal("All", (await ParseAsync(all)).QuerySelector("#money-visitors input[name=Visitors][checked]")?.GetAttribute("value"));
    }

    [Fact]
    public async Task Adding_a_bad_grid_address_is_refused()
    {
        await using var f = Make(new FakeBooks());
        var c = await SignedInAsync(f);

        foreach (var bad in new[] { "", "not an address", "ftp://grid.example:8002/", "/a/path", "http://" })
        {
            using var r = await PostAsync(c, "/money", "AddGrid", new() { ["Address"] = bad });
            Assert.NotNull(ErrorText(await ParseAsync(r)));
        }
        Assert.Empty(f.Grid.CurrentSettings().Money.ListedGrids ?? []);
        Assert.Empty(Results(f, "money-grid-add"));

        using (var ok = await PostAsync(c, "/money", "AddGrid", new() { ["Address"] = "http://HG.FriendGrid.net:8002/" }))
            Assert.Contains("http://hg.friendgrid.net:8002", (await ParseAsync(ok)).QuerySelector("#money-grids")!.TextContent);
        using (var twice = await PostAsync(c, "/money", "AddGrid", new() { ["Address"] = "http://hg.friendgrid.net:8002" })) { }
        Assert.Equal(["http://hg.friendgrid.net:8002"], f.Grid.CurrentSettings().Money.ListedGrids);

        using var gone = await PostAsync(c, "/money", "RemoveGrid", new() { ["Address"] = "http://hg.friendgrid.net:8002" });

        Assert.Empty(f.Grid.CurrentSettings().Money.ListedGrids ?? []);
        Assert.Equal(["http://hg.friendgrid.net:8002"], Targets(f, "money-grid-add"));
        Assert.Equal(["http://hg.friendgrid.net:8002"], Targets(f, "money-grid-remove"));
    }

    [Fact]
    public async Task Settings_post_saves_the_three_new_limits()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { Visitors = VisitorMoney.All });
        var c = await SignedInAsync(f);
        var form = Settings();
        form["DailySpendLimit"] = "12000";
        form["DailyKeySpendLimit"] = "150000";
        form["DailyKeyPayoutLimit"] = "30000";

        using (var r = await PostAsync(c, "/money", "Settings", form))
            Assert.Equal("150000", (await ParseAsync(r)).QuerySelector("input[name=DailyKeySpendLimit]")?.GetAttribute("value"));

        var m = f.Grid.CurrentSettings().Money;
        Assert.Equal((12_000L, 150_000L, 30_000L), (m.DailySpendLimit, m.DailyKeySpendLimit, m.DailyKeyPayoutLimit));
        Assert.Equal(VisitorMoney.All, m.Visitors); // the Visitors section owns visitor money now

        form["DailyKeyPayoutLimit"] = "100000001";
        using var refused = await PostAsync(c, "/money", "Settings", form);

        Assert.NotNull(ErrorText(await ParseAsync(refused)));
        Assert.Equal(30_000, f.Grid.CurrentSettings().Money.DailyKeyPayoutLimit);
    }

    [Fact]
    public async Task Person_page_explains_why_money_is_off()
    {
        await using var f = MakeGrid();
        var c = await SignedInAsync(f, "supervisor");
        var page = $"/money/person?id={Ann}";
        string Here(IHtmlDocument d) => d.QuerySelector("#person-money-here")!.TextContent;

        f.MoneyRegions.Where[Ann] = new SessionLocation(Guid.NewGuid(), Ann, OldDocks, "p1", LocationState.Unverified, DateTime.UtcNow);
        var off = await PageAsync(c, page);
        Assert.Contains("Off.", Here(off));
        Assert.Contains("Old Docks", Here(off));
        Assert.Contains("can't verify", Here(off));
        Assert.Contains("next login", Here(off));
        Assert.NotNull(off.QuerySelector("#person-money-here a[href*='money-verification.md#']"));

        f.MoneyRegions.Where[Ann] = new SessionLocation(Guid.NewGuid(), Ann, NeonPier, KeyOf(Sam), LocationState.Verified, DateTime.UtcNow);
        var on = await PageAsync(c, page);
        Assert.Contains("On.", Here(on));
        Assert.Contains("Neon Pier", Here(on));

        // Verified where they are, but that region has stopped checking in: payments there are refused.
        f.MoneyRegions.Where[Ann] = new SessionLocation(Guid.NewGuid(), Ann, Quay, KeyOf(Jo), LocationState.Verified, DateTime.UtcNow);
        var stale = await PageAsync(c, page);
        Assert.Contains("Off.", Here(stale));
        Assert.Contains("Quay", Here(stale));
        Assert.Contains("Not heard from", Here(stale));
        Assert.DoesNotContain("On.", Here(stale));

        // Handed on to a region that hasn't reported them (it can't verify anyone): paused, like unverified.
        f.MoneyRegions.Where[Ann] = new SessionLocation(Guid.NewGuid(), Ann, OldDocks, "p1", LocationState.Handed, DateTime.UtcNow);
        var handed = await PageAsync(c, page);
        Assert.Contains("Off.", Here(handed));
        Assert.Contains("Moved to Old Docks", Here(handed));
        Assert.Contains("can't verify", Here(handed));
        Assert.NotNull(handed.QuerySelector("#person-money-here a[href*='money-verification.md#']"));

        f.MoneyRegions.Where.Remove(Ann);
        Assert.Contains("Not in the world", Here(await PageAsync(c, page)));
        Assert.Contains($"where {Ann}", f.MoneyRegions.Calls);
    }

    [Fact]
    public async Task My_coins_shows_paused_line_when_unverified()
    {
        await using var f = Make(new FakeBooks());
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        (await c.GetAsync("/__test/guest-signin", Ct)).Dispose();

        f.MoneyRegions.Where[TestAuth.GuestId] = new SessionLocation(Guid.NewGuid(), TestAuth.GuestId, OldDocks, "p1", LocationState.Unverified, DateTime.UtcNow);
        Assert.Equal("Money is paused for you until you next log in, because you came from a region that can't check where you are.",
            (await PageAsync(c, "/guest/coins")).QuerySelector("#coins-paused")?.TextContent.Trim());

        f.MoneyRegions.Where[TestAuth.GuestId] = new SessionLocation(Guid.NewGuid(), TestAuth.GuestId, OldDocks, "p1", LocationState.Handed, DateTime.UtcNow);
        Assert.Equal("Money is paused for you until you next log in, because you moved to a region that can't check where you are.",
            (await PageAsync(c, "/guest/coins")).QuerySelector("#coins-paused")?.TextContent.Trim());

        f.MoneyRegions.Where[TestAuth.GuestId] = new SessionLocation(Guid.NewGuid(), TestAuth.GuestId, Welcome, "p1", LocationState.Verified, DateTime.UtcNow);
        Assert.Null((await PageAsync(c, "/guest/coins")).QuerySelector("#coins-paused"));

        f.MoneyRegions.Down = true; // the rest of the page doesn't depend on it
        Assert.NotNull((await PageAsync(c, "/guest/coins")).QuerySelector("#coins-balance"));
    }

    [Fact]
    public async Task Alerts_show_and_clear()
    {
        var alerts = new FakeAlerts();
        var at = new DateTime(2026, 11, 15, 1, 2, 0, DateTimeKind.Utc);
        alerts.Open.Add(new MoneyAlert(7, AlertKind.UnhandedArrival, AlertLevel.Warning, KeyOf(Sam), NeonPier, Ann, 14, at, at, Repeated: true));
        alerts.Open.Add(new MoneyAlert(8, AlertKind.KeyLimitReached, AlertLevel.Notice, "p1", null, null, 2, at, at));
        alerts.Open.Add(new MoneyAlert(9, AlertKind.UnhandedArrival, AlertLevel.Notice, KeyOf(Sam), NeonPier, Ann, 1, at, at));
        alerts.Open.Add(new MoneyAlert(10, AlertKind.PersonLimitRepeated, AlertLevel.Notice, "p1", Welcome, Ann, 3, at, at));
        // Sam's Neon Pier claimed Ann while the region program p1 had her: one of the two is wrong.
        alerts.Open.Add(new MoneyAlert(11, AlertKind.UnhandedArrival, AlertLevel.Warning, KeyOf(Sam), NeonPier, Ann, 1, at, at, OtherKeyId: "p1"));
        await using var f = MakeGrid(alerts: alerts);
        var c = await SignedInAsync(f);

        var doc = await PageAsync(c, "/money");

        var conflict = doc.QuerySelector("#money-alert-11")!;
        Assert.Contains("Region Server at 10.0.0.5", conflict.TextContent);
        Assert.Contains("and region program p1 disagree about where Ann Smith is", conflict.TextContent);
        // Either may be the honest one: no offer to remove the claimer.
        Assert.Null(conflict.QuerySelector("form[action*='handler=RemoveServer']"));

        var warning = doc.QuerySelector("#money-alert-7")!;
        Assert.Contains("Neon Pier", warning.TextContent);
        Assert.Contains("Region Server at 10.0.0.5", warning.TextContent);
        Assert.Contains("Ann Smith", warning.TextContent);
        Assert.NotNull(warning.QuerySelector($"a[href='/money/person?id={Ann}']"));
        Assert.Equal(Sam.ToString(), warning.QuerySelector("form[action*='handler=RemoveServer'] input[name=Server]")?.GetAttribute("value"));
        Assert.Contains("Remove this server", warning.TextContent);
        // Only a warning about a paired server offers to remove it.
        Assert.Null(doc.QuerySelector("#money-alert-9 form[action*='handler=RemoveServer']"));
        Assert.Contains("Neon Pier", doc.QuerySelector("#money-alert-9")!.TextContent);
        var limit = doc.QuerySelector("#money-alert-8")!;
        Assert.Contains("region program p1", limit.TextContent);
        Assert.NotNull(limit.QuerySelector("a[href='#money-limits']"));
        Assert.Null(limit.QuerySelector("form[action*='handler=RemoveServer']"));
        Assert.Contains("Ann Smith", doc.QuerySelector("#money-alert-10")!.TextContent);
        Assert.Equal(5, doc.QuerySelectorAll("#money-alerts form[action*='handler=ClearAlert']").Length);

        var supervisor = await PageAsync(await SignedInAsync(f, "supervisor"), "/money");
        Assert.NotNull(supervisor.QuerySelector("#money-alert-7"));
        Assert.Empty(supervisor.QuerySelectorAll("#money-alerts form"));

        using var r = await PostAsync(c, "/money", "ClearAlert", new() { ["Id"] = "8" });

        Assert.Contains("clear 8 Test Owner", alerts.Calls);
        Assert.Null((await ParseAsync(r)).QuerySelector("#money-alert-8"));
        Assert.Equal(["ok"], Results(f, "money-alert-clear"));
    }

    [Fact]
    public async Task Robust_outage_still_shows_the_money_page_with_freeze()
    {
        var alerts = new FakeAlerts();
        var at = DateTime.UtcNow;
        alerts.Open.Add(new MoneyAlert(7, AlertKind.UnhandedArrival, AlertLevel.Warning, KeyOf(Sam), NeonPier, Ann, 3, at, at));
        alerts.Open.Add(new MoneyAlert(10, AlertKind.PersonLimitRepeated, AlertLevel.Notice, "p1", Welcome, Ann, 3, at, at));
        await using var f = MakeGrid(alerts: alerts);
        var c = await SignedInAsync(f);
        f.Robust.Down = true;
        var before = f.Robust.Lookups;

        var doc = await PageAsync(c, "/money");

        Assert.NotNull(doc.QuerySelector("form[action*='handler=Freeze']"));
        Assert.Contains($"avatar {Ann}", doc.QuerySelector("#money-alert-7")!.TextContent);
        Assert.Contains($"avatar {Ann}", doc.QuerySelector("#money-alert-10")!.TextContent);
        Assert.Null(doc.QuerySelector($"#money-alerts a[href='/money/person?id={Ann}']"));
        Assert.Equal(1, f.Robust.Lookups - before); // one look per person, however many alerts name them
    }

    /// <summary>Robust's connection breaks while the Money page names who an alert is about: named by id, and Freeze stays.</summary>
    [Fact]
    public async Task A_broken_Robust_connection_still_shows_the_money_page_with_freeze()
    {
        var alerts = new FakeAlerts();
        var at = DateTime.UtcNow;
        alerts.Open.Add(new MoneyAlert(7, AlertKind.UnhandedArrival, AlertLevel.Warning, KeyOf(Sam), NeonPier, Ann, 3, at, at));
        await using var f = MakeGrid(alerts: alerts);
        var c = await SignedInAsync(f);
        f.Robust.OnFindAccountById = () => throw new IOException("The connection was reset.");

        var doc = await PageAsync(c, "/money");

        Assert.NotNull(doc.QuerySelector("form[action*='handler=Freeze']"));
        Assert.Contains($"avatar {Ann}", doc.QuerySelector("#money-alert-7")!.TextContent);
    }

    [Fact]
    public async Task Approved_move_is_audited_even_when_its_notice_cant_be_cleared()
    {
        var alerts = new FakeAlerts { ClearDown = true };
        var at = DateTime.UtcNow;
        alerts.Open.Add(new MoneyAlert(4, AlertKind.RegionMoved, AlertLevel.Notice, KeyOf(Sam), Market, null, 1, at, at));
        await using var f = MakeGrid(alerts: alerts);

        using var r = await PostAsync(await SignedInAsync(f), "/money", "ApproveMove", new() { ["Region"] = Market.ToString(), ["Server"] = Sam.ToString() });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains($"approve {Market} {Sam}", f.MoneyRegions.Calls);
        Assert.Contains("clear 4 Test Owner", alerts.Calls);
        Assert.Equal(["approved"], Results(f, "money-region-move"));
        Assert.Contains("couldn't be cleared", MainText(await ParseAsync(r)));
    }

    [Fact]
    public async Task Settings_without_the_new_limits_is_refused()
    {
        // A Money tab opened before the update posts no daily limits: saving 0 would stop every payment.
        await using var f = Make(new FakeBooks());
        var form = Settings(symbol: "GG");
        form.Remove("DailySpendLimit");
        form.Remove("DailyKeySpendLimit");
        form.Remove("DailyKeyPayoutLimit");

        using var r = await PostAsync(await SignedInAsync(f), "/money", "Settings", form);

        Assert.Contains("daily limits", ErrorText(await ParseAsync(r)));
        var m = f.Grid.CurrentSettings().Money;
        Assert.Equal(("C$", 10_000L, 100_000L, 20_000L), (m.CurrencySymbol, m.DailySpendLimit, m.DailyKeySpendLimit, m.DailyKeyPayoutLimit));
        Assert.Empty(Results(f, "money-settings"));
    }

    [Theory]
    [InlineData(MoneyOutcome.OverDailyLimit)]
    [InlineData(MoneyOutcome.OverKeyLimit)]
    [InlineData(MoneyOutcome.OutcomeUnknown)]
    public void Limit_outcomes_have_their_own_words(MoneyOutcome o) =>
        Assert.DoesNotContain("(", MoneyPageRules.Words(o));

    [Fact]
    public void An_answer_that_may_have_gone_through_tells_staff_to_check_the_history()
    {
        var words = MoneyPageRules.Words(MoneyOutcome.OutcomeUnknown);

        Assert.Contains("may have gone through", words);
        Assert.Contains("history", words);
        Assert.DoesNotContain("couldn't be done", words);
    }
}
