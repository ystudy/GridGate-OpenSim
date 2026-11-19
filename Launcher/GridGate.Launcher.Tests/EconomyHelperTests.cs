using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Economy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Firestorm's Buy currency and Buy land helper, and the public coins page (money plan 2, task 8; money design §8).</summary>
public sealed class EconomyHelperTests
{
    private static readonly Guid Agent = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Secure = Guid.Parse("60000000-0000-0000-0000-000000000006");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSessions : ISecureSessionCheck
    {
        public PresenceOutcome Outcome { get; set; } = PresenceOutcome.Ok;
        public List<(Guid, Guid)> Asked { get; } = [];

        /// <summary>When set, a lookup waits for it before it answers.</summary>
        public Task? Gate { get; set; }

        /// <summary>When set, a lookup fails.</summary>
        public bool Throw { get; set; }

        public async Task<PresenceOutcome> HasSecureSessionAsync(Guid agentId, Guid secureSessionId, CancellationToken ct)
        {
            lock (Asked) Asked.Add((agentId, secureSessionId));
            if (Gate is { } gate) await gate;
            if (Throw) throw new InvalidOperationException("database down");
            return Outcome;
        }
    }

    private static LauncherFactory Make(FakeSessions sessions, MoneyMode mode = MoneyMode.Play, bool open = true, IReadOnlyList<string>? adminNetworks = null,
        IPAddress? caller = null, bool lenientRate = false, EconomyLimits? limits = null, string? defaultLanguage = null) =>
        new(s => s with
        {
            Security = adminNetworks is null ? s.Security : s.Security with { AdminNetworks = adminNetworks },
            GridName = "Avalon",
            Languages = defaultLanguage is null ? s.Languages : s.Languages with { Default = defaultLanguage },
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Money = new MoneySettings(Mode: mode, StartingBalancePlay: 100, WeeklyAllowance: 10),
            // The open gate starts the real money service in the test host: on a port of its own, so tests never meet.
            MoneyPort = 0,
        })
        {
            Before = app =>
            {
                // The test server has no remote address, which counts against the grid-wide second; a real caller always has one.
                app.Use((ctx, next) =>
                {
                    ctx.Connection.RemoteIpAddress = caller ?? IPAddress.Parse("198.18.0.1");
                    return next(ctx);
                });
            },
            ExtraServices = services =>
            {
                services.RemoveAll<ISecureSessionCheck>();
                services.AddSingleton<ISecureSessionCheck>(sessions);
                if (limits is not null)
                {
                    services.RemoveAll<EconomyLimits>();
                    services.AddSingleton(limits);
                }
                if (lenientRate)
                {
                    services.RemoveAll<EconomyLimits>();
                    services.AddSingleton(new EconomyLimits(TimeProvider.System, perSecond: 1000));
                }
                if (open)
                {
                    services.RemoveAll<MoneyGate>();
                    services.AddSingleton(sp => new MoneyGate(sp.GetRequiredService<Func<LauncherSettings>>(), MoneyGate.Open));
                }
            },
        };

    private static string Call(string method, params (string Name, object Value)[] members)
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\"?><methodCall><methodName>{method}</methodName><params><param><value><struct>");
        foreach (var (name, value) in members)
            sb.Append("<member><name>").Append(name).Append("</name><value>").Append(value switch
            {
                int i => $"<int>{i}</int>",
                _ => $"<string>{value}</string>",
            }).Append("</value></member>");
        return sb.Append("</struct></value></param></params></methodCall>").ToString();
    }

    private static (string, object)[] Who(string language = "en", params (string, object)[] more) =>
        [("agentId", Agent.ToString()), ("secureSessionId", Secure.ToString()), ("language", language), .. more];

    private static async Task<(HttpResponseMessage Response, string Xml)> Post(HttpClient client, string path, string body)
    {
        var r = await client.PostAsync(path, new StringContent(body, Encoding.UTF8, "text/xml"), Ct);
        return (r, await r.Content.ReadAsStringAsync(Ct));
    }

    private static HttpClient Client(LauncherFactory f) => f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>Everything Firestorm's parser needs (money design §8.6).</summary>
    private static XElement AssertFirestormSafe(string xml, bool fault = false)
    {
        Assert.StartsWith("<?xml version=\"1.0\"?>", xml);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal("methodResponse", root.Name.LocalName);
        var top = Assert.Single(root.Elements());
        foreach (var b in root.Descendants("boolean")) Assert.Matches("^[01]$", b.Value);
        foreach (var i in root.Descendants("int")) Assert.Matches(@"^-?\d+$", i.Value);
        if (fault)
        {
            Assert.Equal("fault", top.Name.LocalName);
            return top;
        }
        Assert.Equal("params", top.Name.LocalName);
        var s = Assert.Single(Assert.Single(Assert.Single(top.Elements("param")).Elements("value")).Elements("struct"));
        var names = s.Elements("member").Select(m => m.Element("name")!.Value).ToList();
        Assert.Contains("success", names);
        Assert.Contains("errorMessage", names);
        Assert.Contains("errorURI", names);
        Assert.Empty(root.Descendants("fault"));
        return s;
    }

    private static string Member(XElement s, string name) =>
        s.Elements("member").Single(m => m.Element("name")!.Value == name).Element("value")!.Value;

    [Fact]
    public async Task Every_answer_follows_firestorm_rules()
    {
        var sessions = new FakeSessions();
        foreach (var mode in new[] { MoneyMode.Off, MoneyMode.Play })
        {
            await using var f = Make(sessions, mode);
            var c = Client(f);
            foreach (var (path, method, extra) in new (string, string, (string, object)[])[]
                     {
                         ("/economy/currency.php", "getCurrencyQuote", [("currencyBuy", 100)]),
                         ("/economy/currency.php", "buyCurrency", [("currencyBuy", 100), ("estimatedCost", 0), ("confirm", "")]),
                         ("/economy/landtool.php", "preflightBuyLandPrep", [("currencyBuy", 0), ("billableArea", 512)]),
                         ("/economy/landtool.php", "preflightBuyLandPrep", [("currencyBuy", 25), ("billableArea", 512)]),
                         ("/economy/landtool.php", "preflightBuyLandPrep", [("billableArea", 512)]),
                         ("/economy/landtool.php", "buyLandPrep", [("currencyBuy", 25)]),
                     })
                foreach (var outcome in new[] { PresenceOutcome.Ok, PresenceOutcome.NoSession, PresenceOutcome.Unavailable })
                {
                    sessions.Outcome = outcome;
                    var (r, xml) = await Post(c, path, Call(method, Who(more: extra)));
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                    Assert.Equal("text/xml", r.Content.Headers.ContentType!.MediaType);
                    AssertFirestormSafe(xml);
                }
            sessions.Outcome = PresenceOutcome.Ok;
            AssertFirestormSafe((await Post(c, "/economy/currency.php", Call("getCurrencyQuote", ("agentId", "not-a-guid")))).Xml);
            AssertFirestormSafe((await Post(c, "/economy/currency.php", "garbage")).Xml, fault: true);
            AssertFirestormSafe((await Post(c, "/economy/currency.php", Call("preflightBuyLandPrep", Who()))).Xml, fault: true);
            AssertFirestormSafe((await Post(c, "/economy/landtool.php", Call("somethingElse", Who()))).Xml, fault: true);
        }
    }

    [Fact]
    public async Task Preflight_succeeds_when_money_is_off()
    {
        await using var f = Make(new FakeSessions(), MoneyMode.Play, open: false); // chosen, but the gate is closed

        var s = AssertFirestormSafe((await Post(Client(f), "/economy/landtool.php",
            Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 0), ("billableArea", 512)])))).Xml);

        Assert.Equal("1", Member(s, "success"));
        Assert.Equal("", Member(s, "confirm"));
        var names = s.Elements("member").Select(m => m.Element("name")!.Value).ToList();
        Assert.Contains("membership", names);
        Assert.Contains("landUse", names);
        Assert.Contains("currency", names);
    }

    [Fact]
    public async Task Preflight_shortfall_names_the_amount()
    {
        await using var f = Make(new FakeSessions());

        var s = AssertFirestormSafe((await Post(Client(f), "/economy/landtool.php",
            Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 25)])))).Xml);

        Assert.Equal("0", Member(s, "success"));
        Assert.Contains("25", Member(s, "errorMessage"));
        Assert.Contains("C$", Member(s, "errorMessage"));
    }

    [Fact]
    public async Task Shortfall_when_off_says_the_grid_has_no_money()
    {
        await using var f = Make(new FakeSessions(), open: false);

        var s = AssertFirestormSafe((await Post(Client(f), "/economy/landtool.php",
            Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 25)])))).Xml);

        Assert.Equal("0", Member(s, "success"));
        Assert.Equal("This grid doesn't use money, so only free land can be bought.", Member(s, "errorMessage"));
    }

    [Fact]
    public async Task Quote_in_play_mode_refuses_with_help_link()
    {
        await using var f = Make(new FakeSessions());
        var s = f.Store.LoadOrCreate();

        var answer = AssertFirestormSafe((await Post(Client(f), "/economy/currency.php", Call("getCurrencyQuote", Who(more: [("currencyBuy", 100)])))).Xml);

        Assert.Equal("0", Member(answer, "success"));
        Assert.Equal($"http://{Core.Config.HostAddress.Effective(s.Hostname)}:{s.WebPort}/coins", Member(answer, "errorURI"));
        Assert.Contains("C$", Member(answer, "errorMessage"));
        Assert.Contains("play coins", Member(answer, "errorMessage"));
    }

    [Fact]
    public async Task Quote_in_gloebit_mode_points_at_gloebit()
    {
        await using var f = Make(new FakeSessions(), MoneyMode.Gloebit);

        var answer = AssertFirestormSafe((await Post(Client(f), "/economy/currency.php", Call("getCurrencyQuote", Who(more: [("currencyBuy", 100)])))).Xml);

        Assert.Equal("0", Member(answer, "success"));
        Assert.Equal("This grid uses Gloebit. Buy gloebits on Gloebit's website.", Member(answer, "errorMessage"));
        Assert.Equal("https://www.gloebit.com/purchase/", Member(answer, "errorURI"));
    }

    [Fact]
    public async Task Land_shortfall_in_gloebit_mode_points_at_gloebit()
    {
        await using var f = Make(new FakeSessions(), MoneyMode.Gloebit);
        f.Grid.UpdateSettings(s => s with
        {
            Money = s.Money with { Gloebit = new GloebitSettings("sandbox", "0b9e3c2a-1111-4c5d-8e9f-0123456789ab", "7d3f9a10-2222-4b6c-9d8e-fedcba987654", "Owner") },
        }, writeConfig: false);

        var answer = AssertFirestormSafe((await Post(Client(f), "/economy/landtool.php", Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 25)])))).Xml);

        Assert.Equal("0", Member(answer, "success"));
        Assert.Equal("This grid uses Gloebit. Buy gloebits on Gloebit's website.", Member(answer, "errorMessage"));
        Assert.Equal("https://sandbox.gloebit.com/purchase/", Member(answer, "errorURI"));
    }

    [Fact]
    public async Task Coins_page_explains_gloebit()
    {
        await using var f = Make(new FakeSessions(), MoneyMode.Gloebit);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/coins");
        req.Headers.Add("Cookie", "gg-lang=fr");

        using var r = await Client(f).SendAsync(req, Ct);

        var html = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Gloebit", html);
        Assert.Contains("https://www.gloebit.com/", html);
        Assert.DoesNotContain("pièces de jeu", html);
    }

    [Fact]
    public async Task Buy_currency_always_refuses()
    {
        await using var f = Make(new FakeSessions());

        var s = AssertFirestormSafe((await Post(Client(f), "/economy/currency.php",
            Call("buyCurrency", Who(more: [("currencyBuy", 100), ("estimatedCost", 0), ("confirm", "")])))).Xml);

        Assert.Equal("0", Member(s, "success"));
        Assert.Equal("Buying coins in the viewer isn't offered on this grid.", Member(s, "errorMessage"));
    }

    [Fact]
    public async Task Unknown_session_is_told_to_log_in()
    {
        var sessions = new FakeSessions { Outcome = PresenceOutcome.NoSession };
        await using var f = Make(sessions);

        var s = AssertFirestormSafe((await Post(Client(f), "/economy/currency.php", Call("getCurrencyQuote", Who(more: [("currencyBuy", 5)])))).Xml);

        Assert.Equal("0", Member(s, "success"));
        Assert.Equal("Please log in to the grid again, then try once more.", Member(s, "errorMessage"));
        Assert.Equal((Agent, Secure), Assert.Single(sessions.Asked));
    }

    [Fact]
    public async Task Missing_ids_are_never_looked_up()
    {
        var sessions = new FakeSessions();
        await using var f = Make(sessions);

        var s = AssertFirestormSafe((await Post(Client(f), "/economy/currency.php", Call("getCurrencyQuote", ("agentId", Agent.ToString())))).Xml);

        Assert.Equal("0", Member(s, "success"));
        Assert.Empty(sessions.Asked);
    }

    [Fact]
    public async Task Calls_with_missing_ids_never_take_the_second()
    {
        var limits = new EconomyLimits(TimeProvider.System, perSecond: 1);
        await using var f = Make(new FakeSessions(), limits: limits);
        var c = Client(f);

        for (var i = 0; i < 5; i++)
            await Post(c, "/economy/currency.php", Call("getCurrencyQuote", ("agentId", Agent.ToString())));

        Assert.True(limits.TryTakeLookupSecond());
    }

    [Fact]
    public async Task Addresses_sending_unknown_ids_cannot_use_up_the_lookups_of_a_new_viewer()
    {
        var sessions = new FakeSessions { Outcome = PresenceOutcome.NoSession };
        var limits = new EconomyLimits(TimeProvider.System);
        var factories = new List<LauncherFactory>();
        try
        {
            // Twenty-five addresses each send many well-formed calls with fresh random ids.
            for (var i = 1; i <= 25; i++)
            {
                var f = Make(sessions, caller: IPAddress.Parse($"198.51.100.{i}"), limits: limits);
                factories.Add(f);
                var c = Client(f);
                for (var n = 0; n < 10; n++)
                    await Post(c, "/economy/currency.php", Call("getCurrencyQuote", ("agentId", Guid.NewGuid().ToString()), ("secureSessionId", Guid.NewGuid().ToString())));
            }
            // Each address was looked up for only a few of its ids.
            Assert.True(sessions.Asked.Count <= 25 * 3, $"{sessions.Asked.Count} lookups for 250 calls");

            // A new viewer from another address still gets through once its session exists.
            sessions.Outcome = PresenceOutcome.Ok;
            await using var viewer = Make(sessions, caller: IPAddress.Parse("203.0.113.77"), limits: limits);
            var s = AssertFirestormSafe((await Post(Client(viewer), "/economy/currency.php", Call("getCurrencyQuote", Who(more: [("currencyBuy", 5)])))).Xml);
            Assert.DoesNotContain("Too many", Member(s, "errorMessage"));
            Assert.Contains((Agent, Secure), sessions.Asked);
        }
        finally
        {
            foreach (var f in factories) await f.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_address_that_keeps_missing_is_refused_without_a_lookup_but_a_good_session_still_answers()
    {
        var sessions = new FakeSessions();
        var limits = new EconomyLimits(TimeProvider.System);
        await using var f = Make(sessions, limits: limits);
        var c = Client(f);
        Assert.Equal("0", Member(AssertFirestormSafe((await Post(c, "/economy/currency.php", Call("getCurrencyQuote", Who(more: [("currencyBuy", 5)])))).Xml), "success"));
        sessions.Outcome = PresenceOutcome.NoSession;
        for (var n = 0; n < 6; n++)
            await Post(c, "/economy/currency.php", Call("getCurrencyQuote", ("agentId", Guid.NewGuid().ToString()), ("secureSessionId", Guid.NewGuid().ToString())));
        var asked = sessions.Asked.Count;

        var again = AssertFirestormSafe((await Post(c, "/economy/currency.php", Call("getCurrencyQuote", Who(more: [("currencyBuy", 5)])))).Xml);

        Assert.Equal(asked, sessions.Asked.Count);
        Assert.True(asked <= 1 + 3);
        Assert.Equal("0", Member(again, "success"));
    }

    [Fact]
    public async Task Junk_calls_from_twenty_addresses_do_not_lock_out_a_known_viewer()
    {
        var sessions = new FakeSessions();
        var limits = new EconomyLimits(TimeProvider.System);
        await using var f = Make(sessions, limits: limits);
        var c = Client(f);
        var body = Call("getCurrencyQuote", Who(more: [("currencyBuy", 5)]));
        Assert.Equal("0", Member(AssertFirestormSafe((await Post(c, "/economy/currency.php", body)).Xml), "success"));

        for (var i = 1; i <= 20; i++)
        {
            var junk = IPAddress.Parse($"198.51.100.{i}");
            for (var n = 0; n < 3; n++) limits.Allow(junk);
            var other = Call("getCurrencyQuote", ("agentId", Guid.NewGuid().ToString()), ("secureSessionId", "junk"));
            await Post(c, "/economy/currency.php", other);
        }
        var again = AssertFirestormSafe((await Post(c, "/economy/currency.php", body)).Xml);

        Assert.DoesNotContain("Too many", Member(again, "errorMessage"));
        lock (sessions.Asked) Assert.Single(sessions.Asked);
    }

    [Fact]
    public async Task Answer_uses_the_viewers_language()
    {
        await using var f = Make(new FakeSessions());

        var de = AssertFirestormSafe((await Post(Client(f), "/economy/currency.php", Call("getCurrencyQuote", Who("de", ("currencyBuy", 5))))).Xml);
        var xx = AssertFirestormSafe((await Post(Client(f), "/economy/currency.php", Call("getCurrencyQuote", Who("xx", ("currencyBuy", 5))))).Xml);

        Assert.Contains("Spielmünzen", Member(de, "errorMessage"));
        Assert.Contains("play coins", Member(xx, "errorMessage"));
    }

    [Fact]
    public async Task Sixty_one_calls_hit_the_limit()
    {
        await using var f = Make(new FakeSessions(), caller: IPAddress.Parse("203.0.113.9"), lenientRate: true);
        var c = Client(f);
        var body = Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 0)]));

        for (var i = 0; i < 60; i++)
            Assert.Equal("1", Member(AssertFirestormSafe((await Post(c, "/economy/landtool.php", body)).Xml), "success"));
        var last = AssertFirestormSafe((await Post(c, "/economy/landtool.php", body)).Xml);

        Assert.Equal("0", Member(last, "success"));
        Assert.Equal("Too many requests. Please wait a minute and try again.", Member(last, "errorMessage"));
    }

    [Fact]
    public async Task A_refused_caller_is_answered_in_the_owners_language_without_reading_its_body()
    {
        var caller = IPAddress.Parse("203.0.113.9");
        var limits = new EconomyLimits(TimeProvider.System);
        for (var i = 0; i < EconomyLimits.PerMinute; i++) Assert.True(limits.Allow(caller));
        var sessions = new FakeSessions();
        await using var f = Make(sessions, caller: caller, limits: limits, defaultLanguage: "de");
        var c = Client(f);

        // Garbage is answered as a refusal, not a fault, which shows the body was never parsed; the call's own language is ignored.
        var garbage = AssertFirestormSafe((await Post(c, "/economy/currency.php", "not xml at all")).Xml);
        var french = AssertFirestormSafe((await Post(c, "/economy/currency.php", Call("getCurrencyQuote", Who("fr", ("currencyBuy", 5))))).Xml);

        Assert.Equal("0", Member(garbage, "success"));
        Assert.Contains("Zu viele Anfragen", Member(garbage, "errorMessage"));
        Assert.Contains("Zu viele Anfragen", Member(french, "errorMessage"));
        Assert.Empty(sessions.Asked);
    }

    [Fact]
    public async Task Helper_answers_carry_no_per_person_data()
    {
        // The session check only proves some Presence row matches; so an answer may hold the mode, the symbol and the caller's own
        // shortfall, never a balance or anything else about a person.
        await using var f = Make(new FakeSessions());
        var c = Client(f);
        var answers = new List<string>();
        foreach (var (path, method, extra) in new (string, string, (string, object)[])[]
                 {
                     ("/economy/currency.php", "getCurrencyQuote", [("currencyBuy", 100)]),
                     ("/economy/landtool.php", "preflightBuyLandPrep", [("currencyBuy", 0)]),
                     ("/economy/landtool.php", "preflightBuyLandPrep", [("currencyBuy", 25)]),
                 })
            answers.Add((await Post(c, path, Call(method, Who(more: extra)))).Xml);

        foreach (var xml in answers)
        {
            Assert.DoesNotContain(Agent.ToString(), xml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Secure.ToString(), xml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alance", xml);
            var names = XDocument.Parse(xml).Descendants("name").Select(n => n.Value).ToList();
            Assert.All(names, n => Assert.Contains(n, new[] { "success", "errorMessage", "errorURI", "confirm", "membership", "landUse", "currency", "upgrade", "action", "levels", "estimatedCost" }));
        }
    }

    [Fact]
    public async Task A_ninth_lookup_at_once_is_told_too_many()
    {
        var release = new TaskCompletionSource();
        var sessions = new FakeSessions { Gate = release.Task };
        await using var f = Make(sessions);
        var c = Client(f);
        var body = Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 0)]));
        var first = Enumerable.Range(0, 8).Select(_ => Post(c, "/economy/landtool.php", body)).ToList();
        while (true)
        {
            lock (sessions.Asked) if (sessions.Asked.Count == 8) break;
            await Task.Delay(10, Ct);
        }

        var ninth = AssertFirestormSafe((await Post(c, "/economy/landtool.php", body)).Xml);

        Assert.Equal("Too many requests. Please wait a minute and try again.", Member(ninth, "errorMessage"));
        lock (sessions.Asked) Assert.Equal(8, sessions.Asked.Count);
        release.SetResult();
        foreach (var t in first) Assert.Equal("1", Member(AssertFirestormSafe((await t).Xml), "success"));
    }

    [Fact]
    public async Task A_failed_lookup_frees_its_slot()
    {
        var sessions = new FakeSessions { Throw = true };
        await using var f = Make(sessions);
        var c = Client(f);
        var body = Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 0)]));

        for (var i = 0; i < 5; i++) await Post(c, "/economy/landtool.php", body);
        sessions.Throw = false;
        sessions.Outcome = PresenceOutcome.Unavailable;
        for (var i = 0; i < 5; i++) await Post(c, "/economy/landtool.php", body);

        lock (sessions.Asked) Assert.Equal(10, sessions.Asked.Count);
    }

    [Fact]
    public async Task Helper_sets_no_cookies()
    {
        await using var f = Make(new FakeSessions());

        var (r, _) = await Post(Client(f), "/economy/currency.php", Call("getCurrencyQuote", Who(more: [("currencyBuy", 5)])));

        Assert.False(r.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Helper_is_open_outside_admin_networks()
    {
        await using var f = Make(new FakeSessions(), adminNetworks: ["10.0.0.0/8"], caller: IPAddress.Parse("203.0.113.5"));

        var (r, xml) = await Post(Client(f), "/economy/landtool.php", Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 0)])));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("1", Member(AssertFirestormSafe(xml), "success"));
    }

    [Fact]
    public async Task Helper_moves_no_money()
    {
        // It has no money service to call: only the session check is asked anything.
        var sessions = new FakeSessions();
        await using var f = Make(sessions);

        await Post(Client(f), "/economy/currency.php", Call("buyCurrency", Who(more: [("currencyBuy", 100)])));

        Assert.Single(sessions.Asked);
    }

    [Fact]
    public async Task Coins_page_is_anonymous_and_translated()
    {
        await using var f = Make(new FakeSessions());
        var c = Client(f);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/coins");
        req.Headers.Add("Cookie", "gg-lang=fr");

        using var r = await c.SendAsync(req, Ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync(Ct));
        Assert.Contains("pièces de jeu", html);
        Assert.Contains("C$", html);
    }

    [Fact]
    public async Task Coins_page_when_off_says_so()
    {
        await using var f = Make(new FakeSessions(), open: false);

        var html = WebUtility.HtmlDecode(await Client(f).GetStringAsync("/coins", Ct));

        Assert.Contains("This grid doesn't use money.", html);
        Assert.DoesNotMatch(new Regex("play coins"), html);
    }
}
