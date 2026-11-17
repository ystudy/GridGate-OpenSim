using System.Globalization;
using System.Net;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Resident;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Economy;

/// <summary>
/// The economy helper Firestorm's Buy currency and Buy land windows call (money design §8.3-§8.6). It moves no money and
/// asks the money service nothing: it checks the viewer's session, then answers what this grid offers, in the viewer's
/// language. Every answer is a struct with success, errorMessage and errorURI.
/// </summary>
public sealed class EconomyHelper(
    Func<LauncherSettings> settings,
    MoneyGate gate,
    ISecureSessionCheck sessions,
    EconomyLimits limits,
    IStringLocalizer<ResidentText> t,
    OfferedLanguages offered)
{
    public const string CurrencyPath = "/economy/currency.php";
    public const string LandPath = "/economy/landtool.php";

    private static readonly string[] CurrencyMethods = ["getCurrencyQuote", "buyCurrency"];
    private static readonly string[] LandMethods = ["preflightBuyLandPrep", "buyLandPrep"];

    public async Task<string> AnswerAsync(string path, Stream body, IPAddress? caller, CancellationToken ct)
    {
        // Counted before anything is read, so garbage counts too.
        var allowed = limits.Allow(caller);
        var call = XmlRpc.Read(await Buffer(body, ct));
        if (call is null) return XmlRpc.Fault(1, "bad request");
        if (!(path == LandPath ? LandMethods : CurrencyMethods).Contains(call.Method, StringComparer.Ordinal)) return XmlRpc.Fault(2, "unknown method");

        var s = settings();
        // Set here, in this method: a culture set inside an awaited method is dropped when it returns.
        var culture = Languages.Culture(Languages.Match(Text(call, "language"), offered.Current(s)) ?? Languages.English);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        if (!allowed) return Refuse(s, t["Economy.TooMany"]);
        if (!Guid.TryParse(Text(call, "agentId"), out var agent) || !Guid.TryParse(Text(call, "secureSessionId"), out var secure)
            || agent == Guid.Empty || secure == Guid.Empty)
            return Refuse(s, t["Economy.LogInAgain"]);
        if (!limits.TryStartLookup()) return Refuse(s, t["Economy.TooMany"]);
        try
        {
            switch (await sessions.HasSecureSessionAsync(agent, secure, ct))
            {
                case PresenceOutcome.Ok:
                    break;
                case PresenceOutcome.Unavailable:
                    return Refuse(s, t["Economy.Unavailable"]);
                default:
                    return Refuse(s, t["Economy.LogInAgain"]);
            }
        }
        finally
        {
            limits.EndLookup();
        }

        var mode = gate.Effective();
        var symbol = s.Money.CurrencySymbol;
        var shortfall = call.Params.GetValueOrDefault("currencyBuy") is int n ? n : 0;
        return call.Method switch
        {
            // Gloebit sells its own currency (money plan 4): the viewer is sent to Gloebit's purchase page.
            "getCurrencyQuote" or "preflightBuyLandPrep" or "buyLandPrep" when mode == MoneyMode.Gloebit && (call.Method == "getCurrencyQuote" || shortfall > 0) =>
                XmlRpc.Response([("success", false), ("errorMessage", t["Economy.Gloebit"].Value), ("errorURI", GloebitSite(s) + "purchase/")]),
            "getCurrencyQuote" => Refuse(s, mode switch
            {
                MoneyMode.Off => t["Economy.NoMoney"],
                MoneyMode.Play => t["Economy.PlayCoins", symbol],
                _ => t["Economy.RealCoins"],
            }),
            "preflightBuyLandPrep" when shortfall <= 0 => NoShortfall(s),
            "preflightBuyLandPrep" or "buyLandPrep" when shortfall > 0 => Refuse(s, LandShortfall(mode, shortfall, symbol)),
            _ => Refuse(s, t["Economy.BuyNotOffered"]),
        };
    }

    /// <summary>
    /// The body, read without blocking (the server allows no synchronous reads) and cut one byte past the limit, so
    /// <see cref="XmlRpc.Read"/> sees an oversized body as one.
    /// </summary>
    private static async Task<Stream> Buffer(Stream body, CancellationToken ct)
    {
        const int cap = XmlRpc.MaxBytes + 1;
        var copy = new MemoryStream();
        var buffer = new byte[4096];
        int n;
        while (copy.Length < cap && (n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, cap - copy.Length)), ct)) > 0)
            copy.Write(buffer, 0, n);
        copy.Position = 0;
        return copy;
    }

    private string LandShortfall(MoneyMode mode, int shortfall, string symbol) =>
        mode == MoneyMode.Off ? t["Economy.NoMoneyLand"] : t["Economy.Shortfall", shortfall.ToString("N0", CultureInfo.CurrentCulture), symbol];

    private static string? Text(XmlRpc.Call call, string name) => call.Params.GetValueOrDefault(name) as string;

    /// <summary>Gloebit's site for this grid's app: its sandbox for a sandbox app, otherwise the real one.</summary>
    public static string GloebitSite(LauncherSettings s) =>
        s.Money.Gloebit?.Environment == "sandbox" ? "https://sandbox.gloebit.com/" : "https://www.gloebit.com/";

    /// <summary>Where the viewer's OK button goes: the page that says how coins work here.</summary>
    private static string HelpUri(LauncherSettings s) => WebsiteAddress.Base(s) + "/coins";

    private static string Refuse(LauncherSettings s, string message) =>
        XmlRpc.Response([("success", false), ("errorMessage", message), ("errorURI", HelpUri(s))]);

    /// <summary>
    /// The pre-flight with nothing to pay first: no upgrade, no coins to buy and no password, so Firestorm sends the
    /// purchase straight to the region, which takes the coins (money design §8.4).
    /// </summary>
    private static string NoShortfall(LauncherSettings s) =>
        XmlRpc.Response(
        [
            ("success", true),
            ("errorMessage", ""),
            ("errorURI", HelpUri(s)),
            ("confirm", ""),
            ("membership", new (string, object)[] { ("upgrade", false), ("action", ""), ("levels", Array.Empty<(string, object)>()) }),
            ("landUse", new (string, object)[] { ("upgrade", false), ("action", "") }),
            ("currency", new (string, object)[] { ("estimatedCost", 0) }),
        ]);
}
