using System.Globalization;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Money;

/// <summary>
/// The staff pages' words for verified regions, where a person is, and the owner's money alerts (verified-regions design
/// §8.2, §8.8, §8.12), and the headings of the owner's guide (<c>docs/money-verification.md</c>) they link to.
/// </summary>
public static class MoneyVerifyText
{
    /// <summary>The owner's guide (verified-regions design §10).</summary>
    public const string Guide = "https://github.com/ystudy/GridGate-OpenSim/blob/main/docs/money-verification.md";

    // The guide's headings, as GitHub makes their anchors. The guide must keep these headings.
    public const string VerifiedAnchor = "what-verified-means";
    public const string NotPairedAnchor = "not-paired";
    public const string MovedAnchor = "moved-waiting-for-you";
    public const string NoMoneyAnchor = "no-gridgate-money";
    public const string NeedsUpdateAnchor = "needs-an-update";
    public const string NotHeardAnchor = "not-heard-from";
    public const string PausedAnchor = "money-paused-for-one-person";
    public const string VisitorsAnchor = "visitor-money-and-listed-grids";
    public const string LimitsAnchor = "daily-limits";
    public const string OwnLimitsAnchor = "own-limits";
    public const string AlertsAnchor = "alerts";

    /// <summary>A heading of the owner's guide.</summary>
    public static string Link(string anchor) => Guide + "#" + anchor;

    public static string Anchor(RegionStatus status) => status switch
    {
        RegionStatus.Verified => VerifiedAnchor,
        RegionStatus.NotPaired => NotPairedAnchor,
        RegionStatus.Moved => MovedAnchor,
        RegionStatus.NoGridGateMoney => NoMoneyAnchor,
        RegionStatus.NeedsUpdate => NeedsUpdateAnchor,
        _ => NotHeardAnchor,
    };

    /// <summary>A region's status in words; a moved one names the server it belongs to, when that is known.</summary>
    public static string Words(RegionStatus status, string? movedFrom = null) => status switch
    {
        RegionStatus.Verified => "Verified",
        RegionStatus.NotPaired => "Not paired",
        RegionStatus.Moved => movedFrom is null ? "Moved here, waiting for you" : $"Moved here from {movedFrom}, waiting for you",
        RegionStatus.NoGridGateMoney => "No GridGate money",
        RegionStatus.NeedsUpdate => "Needs an update",
        _ => "Not heard from",
    };

    /// <summary>What a status means and what the owner can do about it (§8.2).</summary>
    public static string Help(RegionStatus status) => status switch
    {
        RegionStatus.Verified =>
            "Money works here: its program runs GridGate's money module, says where people go when they leave, and checked in within the last 10 minutes.",
        RegionStatus.NotPaired =>
            "This region is on a Region Server that isn't paired with this grid's money, so money is off there. Pair it under Region Servers.",
        RegionStatus.Moved =>
            "This region belongs to another Region Server but now registers from this one, so money is off there. Approve the move, or keep it off: then money comes back if its own server registers it again.",
        RegionStatus.NoGridGateMoney =>
            "Nothing has checked in with the money service for this region: it may run stock OpenSim, or money isn't set up there. Install GridGate on that server, or leave it without money.",
        RegionStatus.NeedsUpdate =>
            "This region's money module is too old to say where people go, so money is off there. Update GridGate on that server.",
        _ => "This region's program hasn't checked in for over 10 minutes, so money is off there. Check the region is running.",
    };

    /// <summary>What a Region Server's status means, from its money module's last health call.</summary>
    public static string ServerHelp(RegionStatus status) => status switch
    {
        RegionStatus.Verified =>
            $"This server's money module checked in within the last 10 minutes and can say where people go. The money protocol is its module's version; {RegionVerification.HandoffProtocol} or later is needed.",
        RegionStatus.NeedsUpdate => "This server's money module is too old to say where people go, so its regions have no money. Update GridGate on that server.",
        RegionStatus.NotHeardFrom => "This server's money module hasn't checked in for over 10 minutes, so its regions have no money. Check the server is running.",
        _ => "No money module on this server has checked in, so its regions have no money. It may run stock OpenSim, or money isn't set up there.",
    };

    /// <summary>The key of a paired Region Server, as the money service names it.</summary>
    public static string KeyOf(PairedServer server) => "server:" + server.Id.ToString("N");

    /// <summary>A paired Region Server by its address (servers behind one router are told apart by their ports).</summary>
    public static string ServerName(PairedServer server) =>
        server.FirstPort > 0 ? $"Region Server at {server.Host} (ports from {server.FirstPort})" : $"Region Server at {server.Host}";

    /// <summary>A key by the name the owner knows it by: a Region Server by its address, a region program by its name.</summary>
    public static string KeyName(string keyId, LauncherSettings s)
    {
        if (!keyId.StartsWith("server:", StringComparison.Ordinal)) return "region program " + keyId;
        return s.Money.PairedServers?.FirstOrDefault(p => KeyOf(p) == keyId) is { } server ? ServerName(server) : "a Region Server that is no longer paired";
    }

    /// <summary>Where a region runs: this computer, a paired Region Server, or the address of a server nobody paired.</summary>
    public static string Where(RegionState region, LauncherSettings s) =>
        region.KeyId is null ? region.ServerIp
            : region.KeyId.StartsWith("server:", StringComparison.Ordinal) ? KeyName(region.KeyId, s)
            : "This computer";

    /// <summary>
    /// Whether money works for a person now, as their page says it (§8.12, "A person's page"), and the guide heading that
    /// explains why when it doesn't. <paramref name="region"/> is the name of the region they're in.
    /// </summary>
    public static (string Text, string? Anchor) MoneyHere(SessionLocation? where, string? region)
    {
        var place = region ?? "a region";
        return where?.State switch
        {
            null => ("Not in the world right now.", null),
            LocationState.Verified => ($"On. They're in {place}.", null),
            LocationState.LeftGrid => ("Off while they visit another grid. On again when they come back.", PausedAnchor),
            _ => ($"Off. They're in {place}, but they came from a region that can't verify where they are (or two servers disagreed about it). On again at their next login.",
                PausedAnchor),
        };
    }

    /// <summary>What an alert says happened; the page adds who, and what to do (§8.8).</summary>
    public static string Alert(MoneyAlert a, string region, string key)
    {
        var times = a.Count == 1 ? "once" : $"{a.Count.ToString(CultureInfo.InvariantCulture)} times";
        var when = $"Last at {a.LastAt:yyyy-MM-dd HH:mm} UTC.";
        return a.Kind switch
        {
            // A warning can come from the key's count across its regions, or from one claim against another key's place, so
            // this row's own count would understate it.
            AlertKind.UnhandedArrival when a.Level == AlertLevel.Warning =>
                $"{region}, on {key}, claimed people arrived that it wasn't handed. {when}",
            AlertKind.UnhandedArrival =>
                $"{region}, on {key}, claimed someone arrived that it wasn't handed, {times} in a day. A handoff lost on the network looks the same. {when}",
            AlertKind.KeyLimitReached => $"{key} reached its daily money limit, so its payments were refused {times} in a day. {when}",
            AlertKind.RegionMoved => $"{region} now registers from {key}, but it belongs to another server, so money is off there. {when}",
            _ => $"Someone hit their daily spending limit {times} in a day. {when}",
        };
    }
}
