using System.Globalization;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Money;

/// <summary>
/// One person's coins (<c>/money/person?id=…&amp;home=…</c>; money design §9.1): balance, the newest history, whether money
/// works for them right now and their daily limits (verified-regions design §8.12), and for admins give, take, freeze and
/// the script payout switch; the owner may give them limits of their own. A visitor is named by id and home grid, and can
/// be moved to a new home address (money plan 3). Opening the page never creates a wallet. Only while money is on.
/// </summary>
public sealed class PersonModel(MoneyGate gate, IMoneyBooks books, AuditLog audit, IRobustClient robust, IVisitorDirectory visitors,
    Func<LauncherSettings> settings, IMoneyRegions regions, TimeProvider time) : PageModel
{
    /// <summary>Whether money works for them now, in words, and the guide heading that explains why not.</summary>
    public string MoneyHere { get; private set; } = "";
    public string? MoneyHereAnchor { get; private set; }

    /// <summary>Their daily limits and use; null without a wallet.</summary>
    public LimitView? Limits { get; private set; }

    /// <summary>Only the owner gives a person limits of their own (verified-regions design §8.7).</summary>
    public bool CanSetLimits { get; private set; }

    /// <summary>"Daily spending: 2,300 of 10,000 used (grid default)."</summary>
    public static string LimitLine(string what, long used, long limit, bool own, DateTime? until) =>
        $"{what}: {N(used)} of {N(limit)} used ({(own ? "own limit" + (until is { } u ? $" until {u:yyyy-MM-dd HH:mm} UTC" : "") : "grid default")}).";

    /// <summary>
    /// The owner's limits for this person (verified-regions design §8.7): any amount, or empty for the grid default (both
    /// empty goes back to the defaults). An end date is optional: the limits end at 00:00 UTC on it. Needs a reason; audited.
    /// </summary>
    public async Task<IActionResult> OnPostLimitsAsync(long? dailySpend, long? dailyPayout, DateTime? until, CancellationToken ct)
    {
        if (!Owner) return StatusCode(StatusCodes.Status403Forbidden);
        if (await NameAsync(ct) is not { } name) return NotFound();
        var removing = dailySpend is null && dailyPayout is null;
        // The form's date is a day in UTC, so the server's own time zone can't move the end.
        DateTime? end = removing || until is not { } day ? null : DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
        if (string.IsNullOrWhiteSpace(Reason))
        {
            Error = "Say why.";
        }
        else if (!ModelState.IsValid)
        {
            Error = "Each limit must be a whole number, or empty for the grid default, and the end a date.";
        }
        else if (MoneyPageRules.LimitProblem(dailySpend, dailyPayout) is { } problem)
        {
            Error = problem;
        }
        else if (end is { } e && e <= time.GetUtcNow().UtcDateTime)
        {
            Error = "The end date must be a day still to come: the limits end at 00:00 UTC on it.";
        }
        else
        {
            var o = await books.SetPersonLimitsAsync(Who, dailySpend, dailyPayout, end,
                new StaffAction(User.Identity?.Name ?? "staff", LogText.Clean(Reason.Trim(), MoneyPageRules.MaxReason)), ct);
            audit.Write(User, Ip, "money-person-limits", LogText.Clean(name), MoneyPageRules.Code(o) + " " + MoneyPageRules.LimitsText(dailySpend, dailyPayout)
                + (end is { } ends ? " until " + ends.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""));
            if (o == MoneyOutcome.Ok) Notice = removing ? "Back to the grid defaults." : "Saved.";
            else Error = MoneyPageRules.Words(o);
        }
        await ShowAsync(ct);
        return Page();
    }

    public const int HistoryLines = 100;

    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Home { get; set; }
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public string? NewHome { get; set; }

    public string Name { get; private set; } = "";
    public string Symbol { get; private set; } = "";
    public long? Balance { get; private set; }
    public bool NoWallet { get; private set; }
    public bool IsVisitor => !Who.IsResident;
    public AccountFlags? Flags { get; private set; }
    public IReadOnlyList<HistoryLine> History { get; private set; } = [];
    public bool DatabaseDown { get; private set; }
    public bool CanChange { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    private AccountRef Who => new(Id, Home?.Trim() ?? "");

    public static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await ShowAsync(ct) ? Page() : NotFound();

    public Task<IActionResult> OnPostGiveAsync(long amount, CancellationToken ct) =>
        MoveAsync("money-give", (who, staff) => books.GiveAsync(who, amount, staff, ct), amount, ct);

    public Task<IActionResult> OnPostTakeAsync(long amount, CancellationToken ct) =>
        MoveAsync("money-take", (who, staff) => books.TakeAsync(who, amount, staff, ct), amount, ct);

    public Task<IActionResult> OnPostFreezeAccountAsync(CancellationToken ct) => FreezeAsync(true, ct);

    public Task<IActionResult> OnPostUnfreezeAccountAsync(CancellationToken ct) => FreezeAsync(false, ct);

    /// <summary>A visitor's wallet moves to the one under their new home address (money design §7.1); the old one is frozen.</summary>
    public async Task<IActionResult> OnPostMergeAsync(CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (await NameAsync(ct) is not { } name) return NotFound();
        var target = (NewHome ?? "").Trim();
        if (Who.IsResident)
        {
            Error = "Only a visitor's wallet can move to a new home address.";
        }
        else if (string.IsNullOrWhiteSpace(Reason) || !Uri.TryCreate(target, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        {
            Error = "Give the new home address (like http://grid.example:8002/) and say why.";
        }
        else
        {
            var r = await books.MergeWalletsAsync(Who, new AccountRef(Id, target),
                new StaffAction(User.Identity?.Name ?? "staff", LogText.Clean(Reason.Trim(), MoneyPageRules.MaxReason)), ct);
            audit.Write(User, Ip, "money-merge-wallet", LogText.Clean(name), MoneyPageRules.Code(r.Outcome));
            if (r.Outcome == MoneyOutcome.Ok) Notice = "Moved. The old wallet is frozen; the coins are under the new home address.";
            else Error = MoneyPageRules.Words(r.Outcome);
        }
        await ShowAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPayoutsAsync(bool on, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (await NameAsync(ct) is not { } name) return NotFound();
        if (!Who.IsResident)
        {
            Error = "Only residents have scripts that pay out.";
        }
        else
        {
            var r = await books.SetPayoutsAllowedAsync(Id, on, ct);
            audit.Write(User, Ip, "money-payouts-staff", LogText.Clean(name), r.Outcome == MoneyOutcome.Ok ? (on ? "on" : "off") : MoneyPageRules.Code(r.Outcome));
            Done(r.Outcome);
        }
        await ShowAsync(ct);
        return Page();
    }

    private async Task<IActionResult> MoveAsync(string action, Func<AccountRef, StaffAction, Task<MoneyResult>> move, long amount, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (await NameAsync(ct) is not { } name) return NotFound();
        if (string.IsNullOrWhiteSpace(Reason) || amount <= 0)
        {
            Error = "Give an amount and say why.";
        }
        else
        {
            var r = await move(Who, new StaffAction(User.Identity?.Name ?? "staff", LogText.Clean(Reason.Trim(), MoneyPageRules.MaxReason)));
            audit.Write(User, Ip, action, LogText.Clean(name), MoneyPageRules.Code(r.Outcome) + " " + amount.ToString(CultureInfo.InvariantCulture));
            Done(r.Outcome);
        }
        await ShowAsync(ct);
        return Page();
    }

    private async Task<IActionResult> FreezeAsync(bool frozen, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (await NameAsync(ct) is not { } name) return NotFound();
        var action = frozen ? "money-freeze-account" : "money-unfreeze-account";
        try
        {
            await books.SetAccountFrozenAsync(Who, frozen, ct);
            audit.Write(User, Ip, action, LogText.Clean(name), "ok");
            Notice = frozen ? "The account is frozen: it can't pay anyone." : "The account can pay again.";
        }
        catch (MoneyUnavailableException)
        {
            audit.Write(User, Ip, action, LogText.Clean(name), "unavailable");
            Error = MoneyPageRules.Words(MoneyOutcome.Unavailable);
        }
        await ShowAsync(ct);
        return Page();
    }

    private bool Admin => StaffSignIn.RoleOf(User) >= StaffRole.Admin;

    private bool Owner => StaffSignIn.RoleOf(User) == StaffRole.Owner;

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private void Done(MoneyOutcome o)
    {
        if (o == MoneyOutcome.Ok) Notice = MoneyPageRules.Words(o);
        else Error = MoneyPageRules.Words(o);
    }

    /// <summary>
    /// The person's name: a resident's from Robust; a visitor's from the Gatekeeper's record, as "Name (visitor from host)",
    /// or "Visitor from host" for a wallet with no record. Null when there is no such person: a made-up visitor home with no
    /// wallet is a 404, not a page that could make one.
    /// </summary>
    private async Task<string?> NameAsync(CancellationToken ct)
    {
        if (!gate.ServiceHere || Id == Guid.Empty) return null;
        if (Who.IsResident) return (await robust.FindAccountByIdAsync(Id, ct))?.FullName();
        var host = Uri.TryCreate(Who.Home, UriKind.Absolute, out var u) ? u.Host : LogText.Clean(Who.Home);
        try
        {
            if (await visitors.FindAsync(Id, Who.Home, ct) is { } v) return $"{LogText.Clean(v.Name)} (visitor from {host})";
        }
        catch (MoneyUnavailableException)
        {
            // Robust can't be read: fall back to the wallet itself.
        }
        return (await books.FindBalanceAsync(Who, ct)).Outcome == MoneyOutcome.NoWallet ? null : "Visitor from " + host;
    }

    private async Task<bool> ShowAsync(CancellationToken ct)
    {
        if (await NameAsync(ct) is not { } name) return false;
        Name = name;
        Symbol = settings().Money.CurrencySymbol;
        CanChange = Admin;
        CanSetLimits = Owner;
        try
        {
            var balance = await books.FindBalanceAsync(Who, ct);
            Balance = balance.Outcome == MoneyOutcome.Ok ? balance.FromBalance : null;
            NoWallet = balance.Outcome == MoneyOutcome.NoWallet;
            DatabaseDown = balance.Outcome == MoneyOutcome.Unavailable;
            Flags = await books.AccountFlagsAsync(Who, ct);
            History = await books.HistoryAsync(Who, HistoryLines, ct);
            Limits = await books.PersonLimitsAsync(Who, ct);
        }
        catch (MoneyUnavailableException)
        {
            DatabaseDown = true;
        }
        try
        {
            (MoneyHere, MoneyHereAnchor) = await MoneyHereAsync(ct);
        }
        catch (MoneyUnavailableException)
        {
            MoneyHere = "Can't be read right now.";
        }
        return true;
    }

    /// <summary>
    /// Whether money works for them now (verified-regions design §8.12): a visitor from a grid whose visitors can't use money
    /// is told so; anyone else by where their newest logged-in session is (Ruling 3).
    /// </summary>
    private async Task<(string, string?)> MoneyHereAsync(CancellationToken ct)
    {
        var s = settings();
        if (!Who.IsResident && s.Money.EffectiveVisitors == VisitorMoney.Off)
            return ("Off: visitor money is off on this grid.", MoneyVerifyText.VisitorsAnchor);
        if (!Who.IsResident && s.Money.EffectiveVisitors == VisitorMoney.Listed && !s.Money.GridListed(Who.Home))
            return ("Off: their home grid isn't on your list of grids.", MoneyVerifyText.VisitorsAnchor);
        var where = await regions.WhereAgentAsync(Id, ct);
        if (where?.RegionId is not { } id) return MoneyVerifyText.MoneyHere(where, null, null);
        // Verified where they are is not enough: the region itself must still be verified for payments there to work.
        var region = (await regions.AllAsync(s, ct)).FirstOrDefault(r => r.RegionId == id);
        return MoneyVerifyText.MoneyHere(where, region, region?.Name ?? s.Regions.FirstOrDefault(r => r.Uuid == id)?.Name);
    }
}
