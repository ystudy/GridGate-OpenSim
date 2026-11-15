using System.Globalization;
using GridGate.Launcher.Core.Money;

namespace GridGate.Launcher.Money;

/// <summary>What the Money staff pages check before anything is saved or moved (money plan 2, task 9).</summary>
public static class MoneyPageRules
{
    public const long MaxDailyPayoutLimit = 100_000_000;
    public const long MaxMaxPayment = 1_000_000_000;
    public const int MaxReason = 200;


    /// <summary>The modes the page offers: Off, and Play and Gloebit once the gate lets them run. Real is plan 6.</summary>
    public static IReadOnlyList<MoneyMode> Offered(MoneyGate gate) =>
        new[] { MoneyMode.Off, MoneyMode.Play, MoneyMode.Gloebit }.Where(m => m == MoneyMode.Off || gate.Allows(m)).ToList();

    /// <summary>Whether a switch between <paramref name="from"/> and <paramref name="to"/> crosses GridGate coins and Gloebit (design §10).</summary>
    public static bool CrossesGloebit(MoneyMode from, MoneyMode to) =>
        from != to && (from == MoneyMode.Gloebit && to is MoneyMode.Play or MoneyMode.Real || to == MoneyMode.Gloebit && from is MoneyMode.Play or MoneyMode.Real);

    public const string SwitchWarning =
        "GridGate coin balances don't move to Gloebit. They are kept, not shown, and come back if you switch back. Gloebit balances belong to Gloebit.";

    public const string RestartWorld = "Takes effect after you restart GridGate and then the world (Power → Restart on the dashboard).";

    /// <summary>The first problem with <paramref name="m"/>, in words for the page, or null when it can be saved.</summary>
    public static string? Problem(MoneySettings m, IReadOnlyList<MoneyMode> offered)
    {
        if (!offered.Contains(m.Mode)) return "That money mode isn't available here.";
        if (m.Mode == MoneyMode.Gloebit && m.Gloebit?.Complete != true) return "Enter this computer's Gloebit app first, under Gloebit.";
        if (!MoneySymbolRule.Valid(m.CurrencySymbol))
            return "The currency symbol must be 1 to 4 characters, with no spaces and none of ; \" = [ ] < > & \\.";
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
        return null;
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
        MoneyOutcome.DuplicateReference => "That entry was already refunded.",
        _ => "That couldn't be done (" + Code(o) + ").",
    };
}
