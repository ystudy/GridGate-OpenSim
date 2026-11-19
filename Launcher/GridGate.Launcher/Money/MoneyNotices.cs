using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Money;

/// <summary>
/// One notice on the dashboard: its element id and its words. Red unless <paramref name="Info"/>; with
/// <paramref name="MoneyLink"/> it ends with a link to the Money page.
/// </summary>
public sealed record MoneyNotice(string Id, string Text, bool Info = false, bool MoneyLink = false);

/// <summary>
/// The dashboard's money notices (money plan 2, task 9; plan 1's deferred findings): the service didn't start, every
/// payment is frozen, or the money database can't be reached; and each open alert that is a warning.
/// Notices among the alerts are for the Money page. None while money is off.
/// </summary>
public sealed class MoneyNotices(MoneyGate gate, IMoneyBooks books, MoneyStatus status, IMoneyAlerts alerts, Func<LauncherSettings> settings, IRobustClient robust)
{
    /// <summary>At most this many alert warnings are shown one by one; past it, one line says how many more there are.</summary>
    public const int MaxWarnings = 5;

    public async Task<IReadOnlyList<MoneyNotice>> CurrentAsync(CancellationToken ct)
    {
        if (!gate.ServiceHere) return [];
        // Turned on after GridGate started: nothing has failed, the service and its database start with GridGate.
        if (!status.ServiceRunning && status.ServiceError is null)
            return [new("money-restart", "Money was turned on after GridGate started. Restart GridGate and then the world (Power → Restart) for money to start.", Info: true)];
        var notices = new List<MoneyNotice>();
        if (status.ServiceError is not null)
            notices.Add(new("money-service",
                "The money service couldn't start, so payments are refused until GridGate restarts. The launcher log has the details."));
        try
        {
            var (frozen, reason) = await books.FrozenAsync(ct);
            if (frozen) notices.Add(new("money-frozen", $"All payments are frozen: {reason}. Open Money to unfreeze.", MoneyLink: true));
            var warnings = await alerts.WarningsAsync(ct);
            var s = settings();
            foreach (var a in warnings.Take(MaxWarnings))
                notices.Add(new MoneyNotice($"money-alert-{a.Id}", Describe(a, s, a.OtherKeyId is null ? null : await PersonAsync(a.AgentId, ct)), MoneyLink: true));
            if (warnings.Count > MaxWarnings)
                notices.Add(new("money-alert-more", $"{warnings.Count - MaxWarnings} more money warnings. Open Money to see them all.", MoneyLink: true));
        }
        catch (MoneyUnavailableException)
        {
            notices.Add(new("money-database", "The money database can't be reached; payments are refused."));
        }
        return notices;
    }

    /// <summary>
    /// A resident's name for a disagreement's alert, or "avatar" and their id: a visitor isn't a resident, and Robust may be
    /// down. Only the request's own cancellation goes on.
    /// </summary>
    private async Task<string?> PersonAsync(Guid? agent, CancellationToken ct)
    {
        if (agent is not { } id) return null;
        try
        {
            if ((await robust.FindAccountByIdAsync(id, ct))?.FullName() is { } name) return name;
        }
        catch (Exception e) when (RobustFailure.IsUnreachable(e, ct))
        {
            // Named by id instead.
        }
        return $"avatar {id}";
    }

    /// <summary>What an alert says: what happened, where, and that the Money page is where to act on it. <paramref name="person"/> names who a disagreement is about.</summary>
    internal static string Describe(MoneyAlert a, LauncherSettings s, string? person = null)
    {
        var level = a.Level == AlertLevel.Warning ? "Money warning" : "Money notice";
        var times = a.Count == 1 ? "once" : $"{a.Count} times";
        return a.Kind switch
        {
            AlertKind.UnhandedArrival when a.OtherKeyId is { } other =>
                $"{level}: {KeyName(a.KeyId, s)} and {KeyName(other, s)} disagree about where {person ?? "someone"} is. Open Money to check it.",
            // A plain claim is a warning by the key's count of claims across its regions (a disagreement has its own row), so
            // this row's own count would understate it.
            AlertKind.UnhandedArrival when a.Level == AlertLevel.Warning =>
                $"{level}: {KeyName(a.KeyId, s)} claimed avatars arrived in {RegionName(a.RegionId, s)} that it wasn't handed. Open Money to check it.",
            AlertKind.UnhandedArrival =>
                $"{level}: {KeyName(a.KeyId, s)} claimed avatars arrived in {RegionName(a.RegionId, s)} that it wasn't handed, {times} in a day. Open Money to check it.",
            AlertKind.KeyLimitReached =>
                $"{level}: {KeyName(a.KeyId, s)} reached its daily money limit, so its payments were refused {times} in a day. Open Money to check it.",
            AlertKind.RegionMoved =>
                $"{level}: {RegionName(a.RegionId, s)} moved to {KeyName(a.KeyId, s)}; money stays off there until you approve the move. Open Money to check it.",
            AlertKind.SaleNotPaid =>
                $"{level}: a sale in {RegionName(a.RegionId, s)} was completed after its hold ran out, and the buyer couldn't pay it again; the seller wasn't paid. Open Money to check it.",
            AlertKind.ManyPayoutOwners =>
                $"{level}: {KeyName(a.KeyId, s)} paid out for more than {MoneyRules.PayoutOwnersAlertAbove} different object owners in a day. Open Money to check it.",
            AlertKind.ServerTookOver =>
                $"{level}: {KeyName(a.KeyId, s)} took over the earlier server's address and ports. Open Money to check it was you.",
            _ => $"{level}: someone hit their daily spending limit {times} in a day. Open Money to check it.",
        };
    }

    /// <summary>A key by what the owner knows it as: a Region Server by its address, a region program by its name.</summary>
    private static string KeyName(string keyId, LauncherSettings s)
    {
        if (!keyId.StartsWith("server:", StringComparison.Ordinal)) return $"the region program {keyId}";
        var server = s.Money.PairedServers?.FirstOrDefault(p => "server:" + p.Id.ToString("N") == keyId);
        return server is null ? "a Region Server that is no longer paired" : $"the Region Server at {server.Host}";
    }

    private static string RegionName(Guid? id, LauncherSettings s) =>
        s.Regions.FirstOrDefault(r => r.Uuid == id)?.Name is { } name ? $"region {name}" : $"region {id}";
}
