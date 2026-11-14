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
/// One person's coins (<c>/money/person?id=…&amp;home=…</c>; money design §9.1): balance, the newest history, and for
/// admins give, take, freeze and the script payout switch. A visitor is named by id and home grid, and can be moved to a
/// new home address (money plan 3). Opening the page never creates a wallet. Only while money is on.
/// </summary>
public sealed class PersonModel(MoneyGate gate, IMoneyBooks books, AuditLog audit, IRobustClient robust, IVisitorDirectory visitors,
    Func<LauncherSettings> settings) : PageModel
{
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
        if (!gate.On || Id == Guid.Empty) return null;
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
        try
        {
            var balance = await books.FindBalanceAsync(Who, ct);
            Balance = balance.Outcome == MoneyOutcome.Ok ? balance.FromBalance : null;
            NoWallet = balance.Outcome == MoneyOutcome.NoWallet;
            DatabaseDown = balance.Outcome == MoneyOutcome.Unavailable;
            Flags = await books.AccountFlagsAsync(Who, ct);
            History = await books.HistoryAsync(Who, HistoryLines, ct);
        }
        catch (MoneyUnavailableException)
        {
            DatabaseDown = true;
        }
        return true;
    }
}
