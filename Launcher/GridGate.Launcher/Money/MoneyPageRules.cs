using System.Globalization;
using GridGate.Launcher.Core.Money;

namespace GridGate.Launcher.Money;

/// <summary>What the shared Gloebit app form shows: the saved app (never its secret) and where it posts (money plan 4).</summary>
public sealed record GloebitFormModel(GloebitSettings? App, string Action);

/// <summary>What the Money staff pages check before anything is saved or moved (money plan 2, task 9).</summary>
public static class MoneyPageRules
{
    /// <summary>The largest daily limit of any kind, the same bound the ledger puts on an owner's own limits.</summary>
    public const long MaxDailyPayoutLimit = MoneyLedger.MaxDailyLimit;
    public const long MaxMaxPayment = 1_000_000_000;
    public const int MaxReason = 200;


    /// <summary>The modes the page offers: Off, and Play and Gloebit once the gate lets them run. Real is plan 6.</summary>
    public static IReadOnlyList<MoneyMode> Offered(MoneyGate gate) =>
        new[] { MoneyMode.Off, MoneyMode.Play, MoneyMode.Gloebit }.Where(m => m == MoneyMode.Off || gate.Allows(m)).ToList();

    /// <summary>
    /// Whether a switch from <paramref name="from"/> to <paramref name="to"/> crosses GridGate coins and Gloebit (design §10).
    /// Going through Off counts too (money review 2026-11-15, H-5): to Gloebit once coins have ever run here
    /// (<paramref name="coinsUsed"/>), and to coins once a Gloebit app was ever saved (<paramref name="gloebitUsed"/>).
    /// </summary>
    public static bool CrossesGloebit(MoneyMode from, MoneyMode to, bool coinsUsed, bool gloebitUsed)
    {
        static bool Coins(MoneyMode m) => m is MoneyMode.Play or MoneyMode.Real;
        if (from == to) return false;
        if (to == MoneyMode.Gloebit) return Coins(from) || coinsUsed;
        if (Coins(to)) return from == MoneyMode.Gloebit || (!Coins(from) && gloebitUsed);
        return false;
    }

    /// <summary>
    /// Whether only the owner may switch from <paramref name="from"/> to <paramref name="to"/> (design §9.1): Gloebit, and
    /// real coins once they exist, are real dollars, so turning one on is the owner's call, as real coins are (§9.2).
    /// Leaving one puts no dollars at risk, and turning money off is an admin's emergency stop, so admins may do both.
    /// </summary>
    public static bool OwnerOnlySwitch(MoneyMode from, MoneyMode to) => from != to && to is MoneyMode.Gloebit or MoneyMode.Real;

    /// <summary>Why an admin's switch to <paramref name="to"/> was refused.</summary>
    public static string OwnerOnlyText(MoneyMode to) =>
        $"Only the owner can turn on {(to == MoneyMode.Gloebit ? "Gloebit" : "real coins")}.";

    public const string OwnerOnlyNote = "Only the owner can turn on Gloebit or change its Gloebit app.";

    public const string SwitchWarning =
        "GridGate coin balances don't move to Gloebit. They are kept, not shown, and come back if you switch back. Gloebit balances belong to Gloebit.";

    public const string RestartWorld = "Takes effect after you restart GridGate and then the world (Power → Restart on the dashboard).";

    /// <summary>
    /// Money is on in the settings but its service and database haven't started yet, because they start with GridGate:
    /// the dashboard and the Money page say this instead of a database fault.
    /// </summary>
    public const string StartsOnRestart =
        "Money was turned on after GridGate started. Restart GridGate and then the world (Power → Restart) for money to start.";

    /// <summary>The first problem with <paramref name="m"/>, in words for the page, or null when it can be saved.</summary>
    public static string? Problem(MoneySettings m, IReadOnlyList<MoneyMode> offered)
    {
        if (!offered.Contains(m.Mode)) return "That money mode isn't available here.";
        if (m.Mode == MoneyMode.Gloebit && m.Gloebit?.Complete != true) return "Enter this computer's Gloebit app first, under Gloebit.";
        if (!MoneySymbolRule.Valid(m.CurrencySymbol))
            return $"The currency symbol must be 1 to 4 characters, with no spaces and none of {string.Join(' ', MoneySymbolRule.ForbiddenCharacters)}.";
        if (m.CurrencyName.Trim().Length is < 1 or > 20 || m.CurrencyName.Any(char.IsControl))
            return "The currency name must be 1 to 20 characters.";
        if (m.MaxPayment is < 1 or > MaxMaxPayment) return $"The largest payment must be 1 to {MaxMaxPayment.ToString("N0", CultureInfo.InvariantCulture)}.";
        foreach (var (name, value) in new[]
                 {
                     ("starting balance (play)", m.StartingBalancePlay), ("starting balance (real)", m.StartingBalanceReal),
                     ("weekly allowance", m.WeeklyAllowance), ("upload fee", m.UploadFee), ("classified fee", m.ClassifiedFee),
                 })
            if (value < 0 || value > m.MaxPayment)
                return $"The {name} must be 0 to the largest payment ({m.MaxPayment.ToString("N0", CultureInfo.InvariantCulture)}).";
        if (m.DailyPayoutLimit is < 0 or > MaxDailyPayoutLimit)
            return $"The daily payout limit must be 0 to {MaxDailyPayoutLimit.ToString("N0", CultureInfo.InvariantCulture)}.";
        foreach (var (name, value) in new[]
                 {
                     ("daily spending limit", m.DailySpendLimit), ("daily region spending limit", m.DailyKeySpendLimit),
                     ("daily region payout limit", m.DailyKeyPayoutLimit),
                 })
            if (value is < 0 or > MaxDailyPayoutLimit)
                return $"The {name} must be 0 to {MaxDailyPayoutLimit.ToString("N0", CultureInfo.InvariantCulture)}.";
        return null;
    }

    /// <summary>
    /// A Gloebit app from a posted form: an empty secret keeps the stored one. Returns the app and its problem, if any, in
    /// words for the page. Nothing posted is ever echoed back or audited.
    /// </summary>
    public static (GloebitSettings App, string? Problem) GloebitFromForm(GloebitSettings? stored, string? environment, string? key, string? secret,
        string? ownerName, string? ownerEmail)
    {
        var app = new GloebitSettings((environment ?? "").Trim(), (key ?? "").Trim(),
            string.IsNullOrWhiteSpace(secret) ? stored?.Secret ?? "" : secret.Trim(), (ownerName ?? "").Trim(), (ownerEmail ?? "").Trim());
        // The saved secret belongs to the saved key: another app's key needs its own.
        if (string.IsNullOrWhiteSpace(secret) && stored is { Key.Length: > 0 } && !string.Equals(app.Key, stored.Key, StringComparison.OrdinalIgnoreCase))
            return (app, "Type the secret that goes with the new key.");
        return (app, GloebitRules.Problem(app));
    }

    /// <summary>A ledger outcome as the audit log and the page show it: snake case ("not_enough").</summary>
    public static string Code(MoneyOutcome o) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(o.ToString());

    /// <summary>A refusal in words for staff.</summary>
    public static string Words(MoneyOutcome o) => o switch
    {
        MoneyOutcome.Ok => "Done.",
        MoneyOutcome.NotEnough => "There aren't enough coins for that.",
        MoneyOutcome.Frozen => "Payments are frozen. Unfreeze them first.",
        MoneyOutcome.AccountFrozen => "That account is frozen.",
        MoneyOutcome.Unavailable => "The money database can't be reached. Try again later.",
        MoneyOutcome.BadAmount => "That amount or reason isn't allowed.",
        MoneyOutcome.NoWallet => "That person has no wallet here.",
        MoneyOutcome.VisitorsOff => "Visitor wallets are off.",
        MoneyOutcome.VisitorGridNotListed => "That visitor's grid isn't on your list.",
        MoneyOutcome.DuplicateReference or MoneyOutcome.AlreadyRefunded => "That entry was already refunded.",
        MoneyOutcome.UnknownEntry => "There is no entry with that number.",
        MoneyOutcome.NotAVisitor => "Only a visitor's wallet can move to a new home address.",
        MoneyOutcome.OverDailyLimit => "That person has reached their daily limit, which counts the last 24 hours.",
        MoneyOutcome.OverKeyLimit => "That region program or Region Server has reached its daily money limit, which counts the last 24 hours.",
        MoneyOutcome.OutcomeUnknown => "The money database stopped answering while this was being saved, so it may have gone through. Check the history before you try again.",
        _ => "That couldn't be done (" + Code(o) + ").",
    };

    /// <summary>A refused fee refund in words for the owner; the coins come from whoever was paid, so the answer names who lacks them.</summary>
    public static string FeeRefundWords(MoneyResult r) => r.Outcome switch
    {
        MoneyOutcome.Ok => "Refunded. The coins came back from " + (r.PayeeKind == "Fees" ? "Fees." : "the group's founder."),
        MoneyOutcome.NotEnough when r.PayeeKind == "Fees" => "Fees doesn't have the coins to refund this.",
        MoneyOutcome.NotEnough => "The founder doesn't have the coins to refund this.",
        MoneyOutcome.NotAllowed => "That entry isn't an upload, classified or group join fee that this person paid and that went through a hold to the end.",
        _ => Words(r.Outcome),
    };

    /// <summary>
    /// The first problem with an owner's own limits, in words for the page, or null. An empty field (null) uses the grid
    /// default, so only a number outside 0 to <see cref="MaxDailyPayoutLimit"/> is wrong.
    /// </summary>
    public static string? LimitProblem(long? dailySpend, long? dailyPayout) =>
        dailySpend is < 0 or > MaxDailyPayoutLimit || dailyPayout is < 0 or > MaxDailyPayoutLimit
            ? $"Each limit must be 0 to {MaxDailyPayoutLimit.ToString("N0", CultureInfo.InvariantCulture)}, or empty for the grid default."
            : null;

    /// <summary>An owner's own limits as the audit log records them: "spend 50000 payout default", or "default" when both are.</summary>
    public static string LimitsText(long? dailySpend, long? dailyPayout) =>
        dailySpend is null && dailyPayout is null ? "default"
            : $"spend {dailySpend?.ToString(CultureInfo.InvariantCulture) ?? "default"} payout {dailyPayout?.ToString(CultureInfo.InvariantCulture) ?? "default"}";

    /// <summary>The longest grid address the list keeps, the size of a wallet's home column.</summary>
    public const int MaxGridAddress = 255;

    /// <summary>
    /// A listed grid's address as it is stored and compared (as the ledger stores homes), or
    /// null when <paramref name="address"/> isn't an absolute http(s) address with a host.
    /// </summary>
    public static string? GridAddress(string? address)
    {
        var t = (address ?? "").Trim();
        if (t.Length is 0 or > MaxGridAddress || !Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")
            || u.Host.Length == 0 || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0)
            return null;
        return MoneyHomes.Normal(t) is { Length: > 0 } normal ? normal : null;
    }
}
