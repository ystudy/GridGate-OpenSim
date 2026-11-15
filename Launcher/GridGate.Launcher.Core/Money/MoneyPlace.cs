using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// Where money runs (money plan 3). The ledger, its database and the money service live only on a computer that runs
/// Robust, because they check sessions in Robust's database. A Region Server follows the mode its Grid Asset Server
/// publishes; a Connected Server's regions belong to another grid, which keeps its own money (money design §4).
/// </summary>
public static class MoneyPlace
{
    /// <summary>The mode of the grid this computer belongs to, through <paramref name="gate"/> (the release gate, or a test's).</summary>
    public static MoneyMode GridMode(LauncherSettings s, Func<MoneySettings, MoneyMode> gate) => s.Plan switch
    {
        OwnerPlan.RegionServer => gate(s.Money with { Mode = s.Join?.Money ?? MoneyMode.Off }),
        OwnerPlan.Connected => MoneyMode.Off,
        _ => gate(s.Money),
    };

    /// <summary>
    /// Whether this computer's regions run Gloebit's module (money plan 4): the grid's mode is Gloebit and this computer has
    /// a complete Gloebit app of its own. A Connected Server's regions are another grid's and never do.
    /// </summary>
    public static bool GloebitHere(LauncherSettings s, Func<MoneySettings, MoneyMode> gate) =>
        s.Plan != OwnerPlan.Connected && GridMode(s, gate) == MoneyMode.Gloebit && s.Money.Gloebit?.Complete == true;

    /// <summary>Whether GridGate's own ledger and money service run on this computer: Play or Real coins, where Robust runs.</summary>
    public static bool ServiceHere(LauncherSettings s, Func<MoneySettings, MoneyMode> gate) =>
        PlanCatalog.RunsRobust(s) && GridMode(s, gate) is MoneyMode.Play or MoneyMode.Real;
}
