using System.Globalization;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages;

/// <summary>
/// How coins work on this grid, at /coins (money plan 2, task 8): where the viewer's Buy currency and Buy land windows send
/// residents. Open to anyone, in the resident's language; not there on a Region Server or a Connected Server.
/// </summary>
public sealed class CoinsModel(Func<LauncherSettings> settings, MoneyGate gate) : PageModel
{
    public MoneyMode Mode { get; private set; }
    public string Name { get; private set; } = "";
    public string Symbol { get; private set; } = "";
    public long Starting { get; private set; }
    public long Allowance { get; private set; }

    /// <summary>Gloebit's site, in Gloebit mode (money plan 4).</summary>
    public string GloebitSite { get; private set; } = "";

    /// <summary>An amount as residents see it: the symbol, then the number in the page's language.</summary>
    public string Amount(long n) => Symbol + n.ToString("N0", CultureInfo.CurrentCulture);

    public void OnGet()
    {
        var m = settings().Money;
        Mode = gate.Effective();
        (Name, Symbol) = (m.CurrencyName, m.CurrencySymbol);
        Starting = m.StartingBalance(Mode);
        Allowance = Mode == MoneyMode.Play ? m.WeeklyAllowance : 0;
        GloebitSite = Economy.EconomyHelper.GloebitSite(settings());
    }
}
