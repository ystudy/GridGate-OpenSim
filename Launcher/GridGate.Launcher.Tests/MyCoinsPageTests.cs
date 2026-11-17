using System.Net;
using System.Text.Json;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>A resident's My coins page (money plan 2, task 10; money design §9.5).</summary>
public sealed class MyCoinsPageTests
{
    private static readonly Guid Friend = Guid.Parse("f0000000-0000-0000-0000-0000000000f1");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static LauncherFactory Make(FakeBooks books, bool open = true, Func<MoneySettings, MoneySettings>? money = null)
    {
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
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
        f.Robust.Add("Test", "Friend", 0, "pw", Friend);
        return f;
    }

    private static async Task<HttpClient> GuestAsync(LauncherFactory f, string? lang = null)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        if (lang is not null) c.DefaultRequestHeaders.Add("Cookie", "gg-lang=" + lang);
        (await c.GetAsync("/__test/guest-signin", Ct)).Dispose();
        return c;
    }

    private static async Task<IHtmlDocument> PageAsync(HttpClient c, string url = "/guest/coins")
    {
        using var r = await c.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<HttpResponseMessage> PostPayoutsAsync(HttpClient c, bool on)
    {
        var token = (await PageAsync(c)).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return await c.PostAsync("/guest/coins?handler=Payouts",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["On"] = on ? "true" : "false", ["__RequestVerificationToken"] = token }), Ct);
    }

    private static string Text(IHtmlDocument d) => d.QuerySelector("main")!.TextContent;

    private static List<JsonElement> Audit(LauncherFactory f)
    {
        var path = Path.Combine(f.Paths.Logs, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
    }

    [Fact]
    public async Task Page_is_404_when_money_is_off()
    {
        await using var f = Make(new FakeBooks(), open: false);

        using var r = await (await GuestAsync(f)).GetAsync("/guest/coins", Ct);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Page_needs_a_resident_sign_in()
    {
        await using var f = Make(new FakeBooks());

        using var r = await f.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/guest/coins", Ct);

        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Home_links_my_coins_only_when_on()
    {
        await using var on = Make(new FakeBooks());
        await using var off = Make(new FakeBooks(), open: false);

        Assert.NotNull((await PageAsync(await GuestAsync(on), "/guest")).QuerySelector("a[href='/guest/coins']"));
        Assert.Null((await PageAsync(await GuestAsync(off), "/guest")).QuerySelector("a[href='/guest/coins']"));
    }

    [Fact]
    public async Task First_visit_shows_starting_balance()
    {
        var books = new FakeBooks { Balance = 100 };
        await using var f = Make(books);

        var text = Text(await PageAsync(await GuestAsync(f)));

        Assert.Contains("C$100", text);
        Assert.Contains($"balance {TestAuth.GuestId}", books.Calls);
    }

    [Fact]
    public async Task History_shows_names_and_signed_amounts()
    {
        var books = new FakeBooks { Balance = 70 };
        books.History.Add(new HistoryLine(5, DateTimeOffset.UtcNow, EntryType.PayAvatar, AccountRef.Resident(Friend), -30, 70, null, null));
        books.History.Add(new HistoryLine(4, DateTimeOffset.UtcNow, EntryType.PayAvatar, new AccountRef(Guid.NewGuid(), "http://other.example:8002"), 12, 100, null, null));
        await using var f = Make(books);

        var rows = (await PageAsync(await GuestAsync(f))).QuerySelectorAll("#coins-history tbody tr").Select(r => r.TextContent).ToList();

        Assert.Contains(rows, r => r.Contains("Test Friend") && r.Contains("-30") && r.Contains("Payment to or from a person"));
        Assert.Contains(rows, r => r.Contains("Visitor from other.example") && r.Contains("+12"));
        Assert.Contains($"history {TestAuth.GuestId} 50", books.Calls);
    }

    [Fact]
    public async Task Hold_lines_show_no_holding_balance()
    {
        var books = new FakeBooks { Balance = 60 };
        books.History.Add(new HistoryLine(8, DateTimeOffset.UtcNow, EntryType.HoldReturned, null, 40, 100, "dress", null));
        books.History.Add(new HistoryLine(7, DateTimeOffset.UtcNow, EntryType.Hold, null, -40, 60, "dress", null));
        await using var f = Make(books);

        var rows = (await PageAsync(await GuestAsync(f))).QuerySelectorAll("#coins-history tbody tr").ToList();

        // The buyer's hold line stays for good, so it says "Purchase", never "waiting" (final review, Important 5).
        Assert.Equal("Purchase: dress", rows[1].QuerySelector(".what")!.TextContent.Trim());
        Assert.Equal("Purchase returned: dress", rows[0].QuerySelector(".what")!.TextContent.Trim());
        Assert.All(rows, r => Assert.Equal("", r.QuerySelector(".who")!.TextContent.Trim()));
    }

    [Fact]
    public async Task The_sellers_line_says_sale_with_the_object()
    {
        var books = new FakeBooks { Balance = 140 };
        books.History.Add(new HistoryLine(9, DateTimeOffset.UtcNow, EntryType.HoldCompleted, null, 40, 140, "dress", EntryType.ObjectSale));
        books.History.Add(new HistoryLine(10, DateTimeOffset.UtcNow, EntryType.HoldCompleted, null, 50, 190, "parcel 3", EntryType.LandSale));
        await using var f = Make(books);

        var whats = (await PageAsync(await GuestAsync(f))).QuerySelectorAll("#coins-history tbody tr .what").Select(w => w.TextContent.Trim()).ToList();

        Assert.Equal(["Sale: dress", "Land sale: parcel 3"], whats);
    }

    [Fact]
    public async Task A_held_group_fee_reads_as_a_group_join_fee()
    {
        // A fee held before its action: the payer's hold and the founder's completed hold both say what it was, not a sale.
        var books = new FakeBooks { Balance = 80 };
        books.History.Add(new HistoryLine(12, DateTimeOffset.UtcNow, EntryType.HoldCompleted, null, 20, 120, null, EntryType.GroupJoinFee));
        books.History.Add(new HistoryLine(11, DateTimeOffset.UtcNow, EntryType.Hold, null, -20, 80, null, EntryType.GroupJoinFee));
        books.History.Add(new HistoryLine(10, DateTimeOffset.UtcNow, EntryType.Hold, null, -3, 100, null, EntryType.UploadFee));
        await using var f = Make(books);

        var whats = (await PageAsync(await GuestAsync(f))).QuerySelectorAll("#coins-history tbody tr .what").Select(w => w.TextContent.Trim()).ToList();

        Assert.Equal(["Group joining fee", "Group joining fee", "Upload fee"], whats);
    }

    [Fact]
    public async Task Every_entry_type_has_words()
    {
        var books = new FakeBooks();
        foreach (var type in typeof(EntryType).GetFields().Select(x => (string)x.GetValue(null)!))
            books.History.Add(new HistoryLine(1, DateTimeOffset.UtcNow, type, null, 1, 1, null, null));
        await using var f = Make(books);

        var doc = await PageAsync(await GuestAsync(f));

        Assert.All(doc.QuerySelectorAll("#coins-history tbody tr .what"), w => Assert.DoesNotContain("MyCoins.Type", w.TextContent));
    }

    [Fact]
    public async Task Payout_switch_turns_on_and_is_audited()
    {
        var books = new FakeBooks();
        await using var f = Make(books);
        var c = await GuestAsync(f);

        using var r = await PostPayoutsAsync(c, on: true);

        Assert.Contains($"payouts {TestAuth.GuestId} True", books.Calls);
        var line = Assert.Single(Audit(f), a => a.GetProperty("action").GetString() == "money-payouts");
        Assert.Equal("on", line.GetProperty("result").GetString());
        Assert.Equal("guest", line.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Payout_switch_explains_the_daily_limit()
    {
        await using var f = Make(new FakeBooks());

        var en = Text(await PageAsync(await GuestAsync(f)));
        var de = Text(await PageAsync(await GuestAsync(f, "de")));

        Assert.Contains("C$5,000", en);
        Assert.Contains("C$5.000", de);
    }

    [Fact]
    public async Task Grid_wide_off_hides_the_switch()
    {
        await using var f = Make(new FakeBooks(), money: m => m with { ScriptPayouts = false });

        var doc = await PageAsync(await GuestAsync(f));

        Assert.Null(doc.QuerySelector("form[action*='handler=Payouts']"));
        Assert.Contains("Scripts can't pay out on this grid.", Text(doc));
    }

    [Fact]
    public async Task Grid_wide_off_refuses_the_post()
    {
        var books = new FakeBooks();
        await using var f = Make(books, money: m => m with { ScriptPayouts = false });
        var c = await GuestAsync(f);
        var token = (await PageAsync(c)).QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")!;

        using var r = await c.PostAsync("/guest/coins?handler=Payouts",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["On"] = "true", ["__RequestVerificationToken"] = token }), Ct);

        Assert.DoesNotContain(books.Calls, x => x.StartsWith("payouts"));
    }

    [Fact]
    public async Task Unavailable_shows_a_red_box_and_no_numbers()
    {
        await using var f = Make(new FakeBooks { Down = true });

        var doc = await PageAsync(await GuestAsync(f));

        Assert.NotNull(doc.QuerySelector(".alert-danger"));
        Assert.Contains("Your coins can't be shown right now. Please try again later.", Text(doc));
        Assert.Null(doc.QuerySelector("#coins-balance"));
    }
}
