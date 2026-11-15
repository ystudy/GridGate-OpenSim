using GridGate.Launcher.Core.Money;

namespace GridGate.Launcher.Money;

/// <summary>One notice on the dashboard: its element id and its words. Red unless <paramref name="Info"/>.</summary>
public sealed record MoneyNotice(string Id, string Text, bool Info = false);

/// <summary>
/// The dashboard's money notices (money plan 2, task 9; plan 1's deferred findings): the service didn't start, every
/// payment is frozen, or the money database can't be reached. None while money is off.
/// </summary>
public sealed class MoneyNotices(MoneyGate gate, IMoneyBooks books, MoneyStatus status)
{
    public async Task<IReadOnlyList<MoneyNotice>> CurrentAsync(CancellationToken ct)
    {
        if (!gate.ServiceHere) return [];
        // Turned on after GridGate started: nothing has failed, the service and its database start with GridGate.
        if (!status.ServiceRunning && status.ServiceError is null)
            return [new("money-restart", "Money was turned on after GridGate started. Payments start when GridGate restarts.", Info: true)];
        var notices = new List<MoneyNotice>();
        if (status.ServiceError is not null)
            notices.Add(new("money-service",
                "The money service couldn't start, so payments are refused until GridGate restarts. The launcher log has the details."));
        try
        {
            var (frozen, reason) = await books.FrozenAsync(ct);
            if (frozen) notices.Add(new("money-frozen", $"All payments are frozen: {reason}. Open Money to unfreeze."));
        }
        catch (MoneyUnavailableException)
        {
            notices.Add(new("money-database", "The money database can't be reached; payments are refused."));
        }
        return notices;
    }
}
