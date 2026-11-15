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
    public SelfCheckResult Check { get; set; } = new(true, 0, []);

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
        OwnerPlan plan = OwnerPlan.Local)
    {
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Plan = plan,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Money = money?.Invoke(new MoneySettings(Mode: MoneyMode.Play)) ?? new MoneySettings(Mode: MoneyMode.Play),
            MoneyPort = 0,
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services =>
            {
                services.RemoveAll<IMoneyBooks>();
                services.AddSingleton<IMoneyBooks>(books);
                services.RemoveAll<IVisitorDirectory>();
                services.AddSingleton<IVisitorDirectory>(visitors ?? new FakeVisitors());
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
        ["ScriptPayouts"] = "true", ["VisitorWallets"] = "true",
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
        f.Services.GetRequiredService<MoneyStatus>().LastCheck = (f.Time.GetUtcNow(), new SelfCheckResult(true, 0, []));

        var text = MainText(await PageAsync(await SignedInAsync(f), "/money"));

        Assert.Contains("1,000", text);
        Assert.Contains("The books add up.", text);
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
}
