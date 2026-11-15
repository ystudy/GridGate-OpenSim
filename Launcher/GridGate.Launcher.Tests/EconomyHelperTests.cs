using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
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

        public Task<PresenceOutcome> HasSecureSessionAsync(Guid agentId, Guid secureSessionId, CancellationToken ct)
        {
            lock (Asked) Asked.Add((agentId, secureSessionId));
            return Task.FromResult(Outcome);
        }
    }

    private static LauncherFactory Make(FakeSessions sessions, MoneyMode mode = MoneyMode.Play, bool open = true, IReadOnlyList<string>? adminNetworks = null,
        IPAddress? caller = null) =>
        new(s => s with
        {
            Security = adminNetworks is null ? s.Security : s.Security with { AdminNetworks = adminNetworks },
            GridName = "Avalon",
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
                if (caller is not null)
                    app.Use((ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = caller;
                        return next(ctx);
                    });
            },
            ExtraServices = services =>
            {
                services.RemoveAll<ISecureSessionCheck>();
                services.AddSingleton<ISecureSessionCheck>(sessions);
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
        await using var f = Make(new FakeSessions(), caller: IPAddress.Parse("203.0.113.9"));
        var c = Client(f);
        var body = Call("preflightBuyLandPrep", Who(more: [("currencyBuy", 0)]));

        for (var i = 0; i < 60; i++)
            Assert.Equal("1", Member(AssertFirestormSafe((await Post(c, "/economy/landtool.php", body)).Xml), "success"));
        var last = AssertFirestormSafe((await Post(c, "/economy/landtool.php", body)).Xml);

        Assert.Equal("0", Member(last, "success"));
        Assert.Equal("Too many requests. Please wait a minute and try again.", Member(last, "errorMessage"));
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
