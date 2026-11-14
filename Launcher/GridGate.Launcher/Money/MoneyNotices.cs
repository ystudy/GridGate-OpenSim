using GridGate.Launcher.Core.Money;

namespace GridGate.Launcher.Money;

/// <summary>One red notice on the dashboard: its element id and its words.</summary>
public sealed record MoneyNotice(string Id, string Text);

/// <summary>
/// The dashboard's money notices (money plan 2, task 9; plan 1's deferred findings): the service didn't start, every
/// payment is frozen, or the money database can't be reached. None while money is off.
/// </summary>
public sealed class MoneyNotices(MoneyGate gate, IMoneyBooks books, MoneyStatus status)
{
    public async Task<IReadOnlyList<MoneyNotice>> CurrentAsync(CancellationToken ct)
    {
        if (!gate.On) return [];
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
