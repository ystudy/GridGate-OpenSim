namespace GridGate.Launcher.Core.Money;

/// <summary>The numbers the ledger's rules share, in one place.</summary>
public static class MoneyRules
{
    /// <summary>
    /// One key's script payouts in a day, in total, may not pass this many times the per-resident daily payout limit,
    /// whoever the owners are: many owners each within their own limit can't add up to more.
    /// </summary>
    public const long KeyPayoutCapMultiple = 50;

    /// <summary>One key paying out for more than this many different owners in a day raises an alert for the owner.</summary>
    public const int PayoutOwnersAlertAbove = 50;

    /// <summary>Stored answers to transaction ids are kept this long; a retry older than that is not a retry.</summary>
    public static readonly TimeSpan RequestKeep = TimeSpan.FromDays(30);

    /// <summary>
    /// What a key's transaction ids are scoped by, so two programs that happen to use the same id never see each other's
    /// answers. A paired Region Server that gave a computer id is scoped by that id, not by its key: pairing the same computer
    /// again makes a new key, and a retry that spans the new pairing must still find its stored answer. Everything else is
    /// scoped by its key id, as before.
    /// </summary>
    public static string TxnScope(MoneySettings money, RegionKeyGrant grant)
    {
        var machine = grant.ServerHost is null ? null
            : money.PairedServers?.FirstOrDefault(p => "server:" + p.Id.ToString("N") == grant.KeyId)?.MachineId;
        return string.IsNullOrWhiteSpace(machine) ? grant.KeyId : "machine:" + machine.Trim();
    }

    /// <summary>
    /// Whether <paramref name="amount"/> is what the configured fees allow for a fee of <paramref name="type"/>: a classified
    /// fee is exactly the configured fee, an upload fee a positive whole multiple of it. A fee configured as nothing allows
    /// no such charge. Other fee types have no configured amount and pass.
    /// </summary>
    public static bool FeeAmountOk(string type, long amount, MoneySettings settings) => type switch
    {
        EntryType.ClassifiedFee => settings.ClassifiedFee > 0 && amount == settings.ClassifiedFee,
        EntryType.UploadFee => settings.UploadFee > 0 && amount > 0 && amount % settings.UploadFee == 0,
        _ => true,
    };
}
