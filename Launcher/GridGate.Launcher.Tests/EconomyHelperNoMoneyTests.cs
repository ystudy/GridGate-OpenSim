using System.Net;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Economy;
using GridGate.Launcher.Money;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The economy helper moves no money (money design §8.3): it can't reach the ledger, and driven through every method,
/// mode and session answer it never calls it (money review 2026-11-15, H-8; what <c>Helper_moves_no_money</c> meant).
/// </summary>
public sealed class EconomyHelperNoMoneyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Guid Agent = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Secure = Guid.Parse("60000000-0000-0000-0000-000000000006");

    /// <summary>What moves money: the ledger, its interface, and the money service in front of it.</summary>
    private static readonly Type[] MovesMoney = [typeof(IMoneyBooks), typeof(MoneyLedger), typeof(MoneyServiceHost)];

    private static readonly Assembly[] Ours = [typeof(EconomyHelper).Assembly, typeof(MoneyLedger).Assembly];

    private static bool IsOurs(Type t) => Ours.Contains(t.Assembly);

    private static bool Forbidden(Type t) =>
        MovesMoney.Any(m => m.IsAssignableFrom(t)) || (t.IsInterface && IsOurs(t) && t.IsAssignableFrom(typeof(MoneyLedger)));

    /// <summary>
    /// The first chain of constructor parameters from <paramref name="type"/> that reaches something that moves money, or
    /// null. Follows our own classes, every class of ours that implements an interface of ours, and generic arguments (so a
    /// <c>Func&lt;IMoneyBooks&gt;</c> or <c>Lazy&lt;MoneyLedger&gt;</c> counts).
    /// </summary>
    private static string? Reaches(Type type, HashSet<Type> seen, string chain)
    {
        if (!seen.Add(type)) return null;
        if (Forbidden(type)) return chain;
        foreach (var arg in type.IsGenericType ? type.GetGenericArguments() : [])
            if (Reaches(arg, seen, chain + $" <{arg.Name}>") is { } viaArg) return viaArg;
        if (!IsOurs(type)) return null;
        var targets = type.IsInterface
            ? Ours.SelectMany(a => a.GetTypes()).Where(t => t is { IsClass: true, IsAbstract: false } && type.IsAssignableFrom(t))
            : [type];
        foreach (var target in targets)
            foreach (var ctor in target.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                foreach (var p in ctor.GetParameters())
                    if (Reaches(p.ParameterType, seen, chain + $" -> {target.Name}({p.ParameterType.Name} {p.Name})") is { } found)
                        return found;
        return null;
    }

    /// <summary>money review 2026-11-15, H-8: nothing the helper is built from, however deep, is the ledger or the money service.</summary>
    [Fact]
    public void Helper_cannot_reach_the_ledger()
    {
        Assert.Null(Reaches(typeof(EconomyHelper), [], nameof(EconomyHelper)));
    }

    /// <summary>The check above would see a ledger if one were added (so its "nothing found" means something).</summary>
    [Fact]
    public void The_reach_check_finds_a_ledger_where_there_is_one()
    {
        Assert.NotNull(Reaches(typeof(MoneyServiceHost), [], nameof(MoneyServiceHost)));
        Assert.NotNull(Reaches(typeof(GridGate.Launcher.Pages.Guest.CoinsModel), [], "CoinsModel"));
        Assert.NotNull(Reaches(typeof(Func<IMoneyBooks>), [], "Func"));
    }

    /// <summary>A ledger that records any call made to it and then fails it.</summary>
    public class TripWireBooks : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            lock (Calls) Calls.Add(targetMethod?.Name ?? "?");
            throw new InvalidOperationException($"The economy helper called the ledger: {targetMethod?.Name}.");
        }
    }

    private sealed class Sessions : ISecureSessionCheck
    {
        public PresenceOutcome Outcome { get; set; } = PresenceOutcome.Ok;

        public Task<PresenceOutcome> HasSecureSessionAsync(Guid agentId, Guid secureSessionId, CancellationToken ct) => Task.FromResult(Outcome);
    }

    private static (LauncherFactory F, TripWireBooks Books) Make(Sessions sessions, MoneyMode mode, bool open)
    {
        var books = DispatchProxy.Create<IMoneyBooks, TripWireBooks>();
        var wire = (TripWireBooks)(object)books;
        var f = new LauncherFactory(s => s with
        {
            GridName = "Avalon",
            SetupToken = null,
            Plan = OwnerPlan.Local,
            Setup = s.Setup with { WorldNamed = true, OwnerCreated = true, Finished = true, Owner = new OwnerInfo("Test", "Owner", TestAuth.OwnerId) },
            Money = new MoneySettings(Mode: mode, StartingBalancePlay: 100),
            MoneyPort = 0,
        })
        {
            // A request marked "limited" comes from one address, so the per-address limit can be reached; the others have no
            // address and are never limited.
            Before = app => app.Use((ctx, next) =>
            {
                if (ctx.Request.Headers.ContainsKey("X-Limited")) ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
                return next(ctx);
            }),
            ExtraServices = services =>
            {
                services.RemoveAll<IMoneyBooks>();
                services.AddSingleton(books);
                services.RemoveAll<ISecureSessionCheck>();
                services.AddSingleton<ISecureSessionCheck>(sessions);
                services.RemoveAll<EconomyLimits>();
                services.AddSingleton(new EconomyLimits(TimeProvider.System, perSecond: 1000));
                if (open)
                {
                    services.RemoveAll<MoneyGate>();
                    services.AddSingleton(sp => new MoneyGate(sp.GetRequiredService<Func<LauncherSettings>>(), MoneyGate.Open));
                }
            },
        };
        return (f, wire);
    }

    /// <summary>The trip wire is the ledger the host hands out, and it catches a call (so its silence below means something).</summary>
    [Fact]
    public async Task The_trip_wire_is_the_hosts_ledger_and_catches_a_call()
    {
        var (f, books) = Make(new Sessions(), MoneyMode.Play, open: true);
        await using var host = f;

        var ledger = f.Services.GetRequiredService<IMoneyBooks>();

        Assert.Same(books, ledger);
        Assert.Throws<InvalidOperationException>(() => { _ = ledger.TotalsAsync(Ct); });
        lock (books.Calls) Assert.Contains("TotalsAsync", books.Calls);
    }

    private static string Call(string method, int? currencyBuy)
    {
        var sb = new StringBuilder($"<?xml version=\"1.0\"?><methodCall><methodName>{method}</methodName><params><param><value><struct>");
        sb.Append($"<member><name>agentId</name><value><string>{Agent}</string></value></member>");
        sb.Append($"<member><name>secureSessionId</name><value><string>{Secure}</string></value></member>");
        sb.Append("<member><name>language</name><value><string>en</string></value></member>");
        if (currencyBuy is { } n) sb.Append($"<member><name>currencyBuy</name><value><int>{n}</int></value></member>");
        sb.Append("<member><name>estimatedCost</name><value><int>0</int></value></member><member><name>billableArea</name><value><int>512</int></value></member>");
        return sb.Append("</struct></value></param></params></methodCall>").ToString();
    }

    /// <summary>
    /// money review 2026-11-15, H-8: every helper method on both paths, with and without a shortfall, for every session
    /// answer, in every money mode (and with the gate closed), and past the per-address limit: the ledger is never called and
    /// every answer is a well-formed XML-RPC answer.
    /// </summary>
    [Theory]
    [InlineData(MoneyMode.Off, true)]
    [InlineData(MoneyMode.Play, true)]
    [InlineData(MoneyMode.Gloebit, true)]
    [InlineData(MoneyMode.Play, false)]
    public async Task Helper_never_calls_the_ledger(MoneyMode mode, bool open)
    {
        var sessions = new Sessions();
        var (f, books) = Make(sessions, mode, open);
        await using var host = f;
        var c = f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        lock (books.Calls) books.Calls.Clear();
        var answers = 0;

        foreach (var outcome in Enum.GetValues<PresenceOutcome>())
        {
            sessions.Outcome = outcome;
            foreach (var path in new[] { EconomyHelper.CurrencyPath, EconomyHelper.LandPath })
                foreach (var method in new[] { "getCurrencyQuote", "buyCurrency", "preflightBuyLandPrep", "buyLandPrep", "somethingElse" })
                    foreach (var shortfall in new int?[] { null, 0, 25, -5, int.MaxValue })
                    {
                        using var r = await c.PostAsync(path, new StringContent(Call(method, shortfall), Encoding.UTF8, "text/xml"), Ct);
                        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                        var root = XDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).Root!;
                        Assert.Equal("methodResponse", root.Name.LocalName);
                        answers++;
                    }
        }

        // Past the 60 a minute one address may make: the limit's refusal asks nothing either.
        sessions.Outcome = PresenceOutcome.Ok;
        string? last = null;
        for (var i = 0; i <= EconomyLimits.PerMinute; i++)
        {
            using var limited = new HttpRequestMessage(HttpMethod.Post, EconomyHelper.CurrencyPath)
            {
                Content = new StringContent(Call("buyCurrency", 100), Encoding.UTF8, "text/xml"),
            };
            limited.Headers.Add("X-Limited", "1");
            using var r = await c.SendAsync(limited, Ct);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            last = await r.Content.ReadAsStringAsync(Ct);
        }

        Assert.Contains("Too many requests", last);
        Assert.True(answers >= 5 * 2 * 5 * Enum.GetValues<PresenceOutcome>().Length, $"{answers} answers");
        lock (books.Calls) Assert.Empty(books.Calls);
    }
}
