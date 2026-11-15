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

    /// <summary>Whether GridGate's own ledger and money service run on this computer: Play or Real coins, where Robust runs.</summary>
    public static bool ServiceHere(LauncherSettings s, Func<MoneySettings, MoneyMode> gate) =>
        PlanCatalog.RunsRobust(s) && GridMode(s, gate) is MoneyMode.Play or MoneyMode.Real;
}
