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
/// admins give, take, freeze and the script payout switch. A visitor is named by id and home grid. Only while money is on.
/// </summary>
public sealed class PersonModel(MoneyGate gate, IMoneyBooks books, AuditLog audit, IRobustClient robust, Func<LauncherSettings> settings) : PageModel
{
    public const int HistoryLines = 100;

    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty(SupportsGet = true)] public string? Home { get; set; }
    [BindProperty] public string? Reason { get; set; }

    public string Name { get; private set; } = "";
    public string Symbol { get; private set; } = "";
    public long? Balance { get; private set; }
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
            audit.Write(User, Ip, action, LogText.Clean(name), MoneyPageRules.Code(r.Outcome));
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

    /// <summary>The person's name: a resident's from Robust, a visitor's as "Visitor from host". Null when there is no such person.</summary>
    private async Task<string?> NameAsync(CancellationToken ct)
    {
        if (!gate.On || Id == Guid.Empty) return null;
        if (!Who.IsResident)
            return "Visitor from " + (Uri.TryCreate(Who.Home, UriKind.Absolute, out var u) ? u.Host : LogText.Clean(Who.Home));
        return (await robust.FindAccountByIdAsync(Id, ct))?.FullName();
    }

    private async Task<bool> ShowAsync(CancellationToken ct)
    {
        if (await NameAsync(ct) is not { } name) return false;
        Name = name;
        Symbol = settings().Money.CurrencySymbol;
        CanChange = Admin;
        try
        {
            var balance = await books.BalanceAsync(Who, ct);
            Balance = balance.Outcome == MoneyOutcome.Ok ? balance.FromBalance : null;
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
