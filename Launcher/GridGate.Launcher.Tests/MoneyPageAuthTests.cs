using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// Who may post to the money pages (<c>/money</c>, <c>/money/person</c>, <c>/guest/coins</c>, a Region Server's
/// <c>/advanced/money</c>): every POST handler, for every caller who mustn't use it, is refused before it touches the ledger
/// or the settings; a POST without its antiforgery token is a 400; and the staff and guest cookies open only their own
/// side (money review 2026-11-15, H-8).
/// </summary>
public sealed class MoneyPageAuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HtmlParser Parser = new();

    private static readonly Guid Ann = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Hope = Guid.Parse("c0000000-0000-0000-0000-0000000000c1");
    private const string HopeHome = "http://grid.example:8002/";
    private static readonly Guid Server = Guid.Parse("d0000000-0000-0000-0000-0000000000d1");
    private const string GKey = "e0000000-0000-0000-0000-00000000000e";
    private const string GSecret = "f0000000-0000-0000-0000-00000000000f";

    private static readonly string AnnPage = $"/money/person?id={Ann}";
    private static readonly string HopePage = $"/money/person?id={Hope}&home=" + Uri.EscapeDataString(HopeHome);

    /// <summary>Where a page runs: a Grid Asset Server with money on, or a Region Server on a coins grid or a Gloebit grid.</summary>
    public enum Site { GridAsset, RegionCoins, RegionGloebit }

    /// <summary>What an allowed caller's POST does, so the refusals below are known to guard something.</summary>
    private enum Effect { Books, Settings, Pending, PairClient }

    /// <summary>One POST handler, with a form that would act if the caller were allowed.</summary>
    private sealed record Case(string Page, string Handler, Site Site, Dictionary<string, string> Form, Effect Effect, string? BooksCall = null,
        bool CodePending = false);

    private static Dictionary<string, string> Why(Dictionary<string, string>? more = null) =>
        new(more ?? []) { ["Reason"] = "checking a report" };

    private static readonly Case[] Handlers =
    [
        new("/money", "Settings", Site.GridAsset, new()
        {
            ["Mode"] = "Play", ["CurrencySymbol"] = "GG", ["CurrencyName"] = "coins", ["StartingBalancePlay"] = "100", ["StartingBalanceReal"] = "0",
            ["WeeklyAllowance"] = "0", ["UploadFee"] = "0", ["ClassifiedFee"] = "0", ["DailyPayoutLimit"] = "5000", ["MaxPayment"] = "1000000",
            ["ScriptPayouts"] = "true", ["VisitorWallets"] = "true",
        }, Effect.Settings),
        new("/money", "Gloebit", Site.GridAsset, new()
        {
            ["Environment"] = "sandbox", ["Key"] = GKey, ["Secret"] = GSecret, ["OwnerName"] = "Grid Owner", ["OwnerEmail"] = "owner@example.org",
        }, Effect.Settings),
        new("/money", "PairingCode", Site.GridAsset, [], Effect.Pending),
        new("/money", "PairingCancel", Site.GridAsset, [], Effect.Pending, CodePending: true),
        new("/money", "RemoveServer", Site.GridAsset, new() { ["Server"] = Server.ToString() }, Effect.Settings),
        new("/money", "Check", Site.GridAsset, [], Effect.Books, "self-check"),
        new("/money", "Freeze", Site.GridAsset, Why(), Effect.Books, "freeze True"),
        new("/money", "Unfreeze", Site.GridAsset, Why(), Effect.Books, "freeze False"),
        new("/money", "Refund", Site.GridAsset, Why(new() { ["EntryId"] = "42" }), Effect.Books, "refund 42"),
        new("/money", "MoveFees", Site.GridAsset, Why(new() { ["Name"] = "Ann Smith", ["Amount"] = "5" }), Effect.Books, "move-fees"),
        new(AnnPage, "Give", Site.GridAsset, Why(new() { ["Amount"] = "5" }), Effect.Books, "give"),
        new(AnnPage, "Take", Site.GridAsset, Why(new() { ["Amount"] = "5" }), Effect.Books, "take"),
        new(AnnPage, "FreezeAccount", Site.GridAsset, Why(), Effect.Books, $"freeze-account {Ann} True"),
        new(AnnPage, "UnfreezeAccount", Site.GridAsset, Why(), Effect.Books, $"freeze-account {Ann} False"),
        new(AnnPage, "Payouts", Site.GridAsset, new() { ["On"] = "true" }, Effect.Books, "payouts"),
        new(HopePage, "Merge", Site.GridAsset, Why(new() { ["NewHome"] = "http://new.example:8002/" }), Effect.Books, "merge"),
        new("/guest/coins", "Payouts", Site.GridAsset, new() { ["On"] = "true" }, Effect.Books, "payouts"),
        new("/advanced/money", "Pair", Site.RegionCoins, new() { ["Code"] = "7K2M-9QXD-4HTB-ZW3R" }, Effect.PairClient),
        new("/advanced/money", "Check", Site.RegionCoins, [], Effect.PairClient),
        new("/advanced/money", "Unpair", Site.RegionCoins, [], Effect.Settings),
        new("/advanced/money", "Gloebit", Site.RegionGloebit, new()
        {
            ["Environment"] = "sandbox", ["Key"] = GKey, ["Secret"] = GSecret, ["OwnerName"] = "RS Owner", ["OwnerEmail"] = "",
        }, Effect.Settings),
    ];

    private static Case Find(string page, string handler) => Handlers.Single(c => c.Page == page && c.Handler == handler);

    /// <summary>The page's own guest (for <c>/guest/coins</c>) or owner may post; these may not.</summary>
    private static string[] Refused(Case c) => c.Page == "/guest/coins" ? ["anonymous", "supervisor", "owner"] : ["anonymous", "guest", "supervisor"];

    private static string Allowed(Case c) => c.Page == "/guest/coins" ? "guest" : "owner";

    public static TheoryData<string, string, string> RefusedPosts()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var c in Handlers)
            foreach (var who in Refused(c))
                data.Add(c.Page, c.Handler, who);
        return data;
    }

    public static TheoryData<string, string> AllPosts()
    {
        var data = new TheoryData<string, string>();
        foreach (var c in Handlers) data.Add(c.Page, c.Handler);
        return data;
    }

    // ---- the test host ----

    private sealed class CountingPairClient : IMoneyPairClient
    {
        public List<string> Calls { get; } = [];

        public Task<PairOutcome> PairAsync(LauncherSettings s, string typedCode, CancellationToken ct)
        {
            lock (Calls) Calls.Add("pair");
            return Task.FromResult(new PairOutcome(null, "refused"));
        }

        public Task<PairingHealth> CheckAsync(LauncherSettings s, CancellationToken ct)
        {
            lock (Calls) Calls.Add("check");
            return Task.FromResult(PairingHealth.Ok);
        }
    }

    private sealed record Host(LauncherFactory F, FakeBooks Books, CountingPairClient Pair) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => F.DisposeAsync();
    }

    private static JoinedGrid Avalon(MoneyMode mode) =>
        new("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", LauncherVersion.Current, false, 8004, 8006,
            mode, 8000, new string('A', 64), "http://192.168.1.5:8001/economy/");

    private static Host Make(Site site)
    {
        var books = new FakeBooks();
        var pair = new CountingPairClient();
        var visitors = new FakeVisitors();
        visitors.Known.Add(new VisitorInfo(Hope, HopeHome, "Hope Visitor"));
        var region = site != Site.GridAsset;
        var f = new LauncherFactory(s => s with
        {
            SetupToken = null,
            GridName = "Avalon",
            Plan = region ? OwnerPlan.RegionServer : OwnerPlan.GridAsset,
            Join = region ? Avalon(site == Site.RegionGloebit ? MoneyMode.Gloebit : MoneyMode.Play) : null,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Money = region
                ? new MoneySettings(Pairing: new ServerPairing("192.168.1.5", new string('A', 64), "the-key", DateTimeOffset.UtcNow))
                : new MoneySettings(Mode: MoneyMode.Play, ScriptPayouts: true, VisitorWallets: true,
                    PairedServers: [new PairedServer(Server, "192.168.1.20", new string('B', 64), DateTimeOffset.UtcNow)]),
            MoneyPort = 0,
        })
        {
            Before = TestAuth.Apply,
            ExtraServices = services =>
            {
                services.RemoveAll<IMoneyBooks>();
                services.AddSingleton<IMoneyBooks>(books);
                services.RemoveAll<IVisitorDirectory>();
                services.AddSingleton<IVisitorDirectory>(visitors);
                services.RemoveAll<IMoneyPairClient>();
                services.AddSingleton<IMoneyPairClient>(pair);
                services.RemoveAll<MoneyGate>();
                services.AddSingleton(sp => new MoneyGate(sp.GetRequiredService<Func<LauncherSettings>>(), MoneyGate.Open));
            },
        };
        TestAuth.AddStaff(f.Robust);
        f.Robust.Add("Ann", "Smith", 0, "pw", Ann);
        return new Host(f, books, pair);
    }

    /// <summary>A client signed in as <paramref name="who"/>: anonymous, guest (a resident's guest cookie), supervisor, admin or owner.</summary>
    private static async Task<HttpClient> ClientAsync(LauncherFactory f, string who)
    {
        var c = f.CreateClient(new() { AllowAutoRedirect = false });
        var path = who switch
        {
            "anonymous" => null,
            "guest" => "/__test/guest-signin",
            _ => "/__test/signin?role=" + who,
        };
        if (path is not null)
        {
            using var r = await c.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }
        return c;
    }

    /// <summary>
    /// A real antiforgery token for this client. The staff pages check it against the staff cookie's person, which the
    /// sign-in page sees too; the guest pages against the guest cookie's, which only a guest page sees (and only where
    /// there is a guest side).
    /// </summary>
    private static async Task<string> TokenAsync(HttpClient c, string who, Site site)
    {
        using var r = await c.GetAsync(who == "guest" && site == Site.GridAsset ? "/guest/coins" : "/login", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var token = Parser.ParseDocument(await r.Content.ReadAsStringAsync(Ct)).QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value");
        Assert.NotNull(token);
        return token;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, Case k, string? token)
    {
        var form = new Dictionary<string, string>(k.Form);
        if (token is not null) form["__RequestVerificationToken"] = token;
        var sep = k.Page.Contains('?') ? '&' : '?';
        return await c.PostAsync($"{k.Page}{sep}handler={k.Handler}", new FormUrlEncodedContent(form), Ct);
    }

    /// <summary>
    /// Everything a money POST could change: the ledger, the pairing client, the settings, a pending pairing code, and the
    /// audit log's actions. The "request" line every staff POST gets (with its status) records the attempt, not a change, so
    /// it isn't counted.
    /// </summary>
    private sealed record Seen(int BooksCalls, int PairCalls, int SettingsUpdates, string SettingsHash, DateTimeOffset? Pending, string Actions);

    private static Seen Look(Host h)
    {
        static string Read(string path)
        {
            if (!File.Exists(path)) return "";
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(s).ReadToEnd();
        }
        int books, pair, updates;
        lock (h.Books.Calls) books = h.Books.Calls.Count;
        lock (h.Pair.Calls) pair = h.Pair.Calls.Count;
        lock (h.F.Grid.UpdateWriteConfig) updates = h.F.Grid.UpdateWriteConfig.Count;
        var actions = string.Join(" ", Read(Path.Combine(h.F.Paths.Logs, "audit.log")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("action").GetString())
            .Where(a => a != "request"));
        return new Seen(books, pair, updates, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Read(h.F.Paths.SettingsFile))))[..16], h.F.Services.GetRequiredService<MoneyPairing>().PendingUntil, actions);
    }

    private static List<string> BooksSince(Host h, Seen before)
    {
        lock (h.Books.Calls) return h.Books.Calls.Skip(before.BooksCalls).ToList();
    }

    private static void Arrange(Host h, Case k)
    {
        if (k.CodePending) h.F.Services.GetRequiredService<MoneyPairing>().Issue();
    }

    /// <summary>The path a redirect goes to.</summary>
    private static string? RedirectPath(HttpResponseMessage r) =>
        r.Headers.Location is { } l ? new Uri(new Uri("http://localhost"), l).AbsolutePath : null;

    private static void AssertSentToSignIn(HttpResponseMessage r, string signIn, string what)
    {
        Assert.True(r.StatusCode == HttpStatusCode.Redirect, $"{what}: {(int)r.StatusCode}, expected a redirect to {signIn}");
        Assert.Equal(signIn, RedirectPath(r));
    }

    // ---- every POST handler, refused ----

    /// <summary>
    /// money review 2026-11-15, H-8: every POST handler on the money pages, for anonymous, a resident's guest cookie and a
    /// supervisor (and on the guest page, for staff cookies), with a valid antiforgery token and a form that would act: the
    /// request is refused (sent to sign in, or 403) and nothing reaches the ledger, the settings, the pairing or the audit log.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedPosts))]
    public async Task Post_is_refused_and_changes_nothing(string page, string handler, string who)
    {
        var k = Find(page, handler);
        await using var h = Make(k.Site);
        Arrange(h, k);
        var c = await ClientAsync(h.F, who);
        var token = await TokenAsync(c, who, k.Site);
        var before = Look(h);

        using var r = await PostAsync(c, k, token);

        var what = $"{who} POST {page} handler={handler}";
        if (page == "/guest/coins")
            AssertSentToSignIn(r, "/guest/sign-in", what);
        else if (who == "supervisor")
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden, $"{what}: {(int)r.StatusCode}, expected 403");
        else
            AssertSentToSignIn(r, "/login", what);
        var after = Look(h);
        Assert.True(before == after, $"{what} changed something: books +{string.Join(", ", BooksSince(h, before))}; before {before}; after {after}");
    }

    /// <summary>
    /// money review 2026-11-15, H-8: the same requests from the page's own caller (the owner, or the resident on My coins)
    /// get 200 and do what the form says, so the refusals above are known to stand in front of something real.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPosts))]
    public async Task Same_post_from_the_right_person_acts(string page, string handler)
    {
        var k = Find(page, handler);
        await using var h = Make(k.Site);
        Arrange(h, k);
        var who = Allowed(k);
        var c = await ClientAsync(h.F, who);
        var token = await TokenAsync(c, who, k.Site);
        var before = Look(h);

        using var r = await PostAsync(c, k, token);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var after = Look(h);
        switch (k.Effect)
        {
            case Effect.Books:
                Assert.Contains(BooksSince(h, before), x => x.StartsWith(k.BooksCall!, StringComparison.Ordinal));
                break;
            case Effect.Settings:
                Assert.True(after.SettingsUpdates > before.SettingsUpdates, $"no settings change; {after}");
                break;
            case Effect.Pending:
                Assert.NotEqual(before.Pending, after.Pending);
                break;
            case Effect.PairClient:
                Assert.True(after.PairCalls > before.PairCalls, $"the pairing client wasn't asked; {after}");
                break;
        }
    }

    /// <summary>
    /// money review 2026-11-15, H-8: the list above is every POST handler the four money pages have (<c>OnPost&lt;Name&gt;</c>
    /// and <c>OnPost&lt;Name&gt;Async</c>), so a new handler can't be added without a refusal test.
    /// </summary>
    [Fact]
    public void Every_money_post_handler_is_listed()
    {
        static IEnumerable<string> Posts(Type page) =>
            page.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.StartsWith("OnPost", StringComparison.Ordinal))
                .Select(m => m.Name["OnPost".Length..] is var n && n.EndsWith("Async", StringComparison.Ordinal) ? n[..^"Async".Length] : m.Name["OnPost".Length..])
                .Order();

        static IEnumerable<string> Listed(Func<string, bool> page) => Handlers.Where(c => page(c.Page)).Select(c => c.Handler).Order();

        Assert.Equal(Posts(typeof(GridGate.Launcher.Pages.Money.IndexModel)), Listed(p => p == "/money"));
        Assert.Equal(Posts(typeof(GridGate.Launcher.Pages.Money.PersonModel)), Listed(p => p.StartsWith("/money/person", StringComparison.Ordinal)));
        Assert.Equal(Posts(typeof(GridGate.Launcher.Pages.Guest.CoinsModel)), Listed(p => p == "/guest/coins"));
        Assert.Equal(Posts(typeof(GridGate.Launcher.Pages.Advanced.MoneyModel)), Listed(p => p == "/advanced/money"));
    }

    // ---- antiforgery ----

    public static TheoryData<string, string, string> TokenlessPosts() => new()
    {
        { "/money", "Freeze", "missing" },
        { "/money", "Freeze", "foreign" },
        { AnnPage, "Give", "missing" },
        { AnnPage, "Give", "foreign" },
        { "/guest/coins", "Payouts", "missing" },
        { "/guest/coins", "Payouts", "foreign" },
        { "/advanced/money", "Unpair", "missing" },
        { "/advanced/money", "Unpair", "foreign" },
    };

    /// <summary>
    /// money review 2026-11-15, H-8: on each money page, the page's own caller posting without the antiforgery token, or
    /// with one minted for another browser, gets 400 and changes nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(TokenlessPosts))]
    public async Task Post_without_its_antiforgery_token_is_400(string page, string handler, string token)
    {
        var k = Find(page, handler);
        await using var h = Make(k.Site);
        var who = Allowed(k);
        var c = await ClientAsync(h.F, who);
        // The browser has visited (it holds the antiforgery cookie); only the form's token is missing or someone else's.
        _ = await TokenAsync(c, who, k.Site);
        var other = await TokenAsync(await ClientAsync(h.F, "anonymous"), "anonymous", k.Site);
        var before = Look(h);

        using var r = await PostAsync(c, k, token == "foreign" ? other : null);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var after = Look(h);
        Assert.True(before == after, $"{who} POST {page} handler={handler} ({token} token) changed something; before {before}; after {after}");
    }

    // ---- which cookie opens which side ----

    /// <summary>
    /// money review 2026-11-15, H-8: a resident signed in at the guest door can't open the staff Money pages: the guest
    /// cookie isn't read there, so they're sent to the staff sign-in, and the ledger isn't asked anything.
    /// </summary>
    [Theory]
    [InlineData("/money")]
    [InlineData("/money/person?id=a0000000-0000-0000-0000-0000000000a1")]
    [InlineData("/money?Q=Ann")]
    public async Task Residents_guest_cookie_does_not_open_the_money_page(string url)
    {
        await using var h = Make(Site.GridAsset);
        var c = await ClientAsync(h.F, "guest");
        using (var mine = await c.GetAsync("/guest/coins", Ct)) Assert.Equal(HttpStatusCode.OK, mine.StatusCode); // the cookie works
        var before = Look(h);

        using var r = await c.GetAsync(url, Ct);

        AssertSentToSignIn(r, "/login", "guest GET " + url);
        Assert.Empty(BooksSince(h, before));
    }

    /// <summary>
    /// money review 2026-11-15, H-8: a staff cookie (owner, admin or supervisor) can't open a resident's My coins page: it's
    /// sent to the guest sign-in, and no wallet is looked up or made for the staff member.
    /// </summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("supervisor")]
    public async Task Staff_cookie_does_not_open_my_coins(string role)
    {
        await using var h = Make(Site.GridAsset);
        var c = await ClientAsync(h.F, role);
        using (var staff = await c.GetAsync("/money", Ct)) Assert.Equal(HttpStatusCode.OK, staff.StatusCode); // the cookie works
        var before = Look(h);

        using var r = await c.GetAsync("/guest/coins", Ct);

        AssertSentToSignIn(r, "/guest/sign-in", role + " GET /guest/coins");
        Assert.Empty(BooksSince(h, before));
    }
}
