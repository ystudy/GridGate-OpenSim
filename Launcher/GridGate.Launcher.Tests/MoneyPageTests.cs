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

/// <summary>The Money staff page, the person page and the dashboard's money notices (money plan 2, task 9).</summary>
public sealed class MoneyPageTests
{
    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static LauncherFactory Make(FakeBooks books, bool open = true, Func<MoneySettings, MoneySettings>? money = null)
    {
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            Plan = OwnerPlan.Local,
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
    public async Task Open_gate_offers_off_and_play_only()
    {
        await using var f = Make(new FakeBooks());

        var doc = await PageAsync(await SignedInAsync(f), "/money");

        Assert.Equal(["Off", "Play"], doc.QuerySelectorAll("select[name=Mode] option").Select(o => o.GetAttribute("value")));
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
        Assert.Contains("The new mode takes effect when GridGate restarts.", MainText(await ParseAsync(r)));
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
        Assert.Contains(Audit(f), a => a.GetProperty("action").GetString() == "money-give" && a.GetProperty("result").GetString() == "frozen");
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
        f.Services.GetRequiredService<MoneyStatus>().ServiceError = "IOException";

        var doc = await PageAsync(await SignedInAsync(f), "/dashboard");

        Assert.Contains("The money service couldn't start, so payments are refused until GridGate restarts. The launcher log has the details.",
            doc.QuerySelector("#money-service")!.TextContent);
    }

    [Fact]
    public async Task Dashboard_shows_database_down()
    {
        await using var f = Make(new FakeBooks { Down = true });

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
