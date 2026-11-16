using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Grid;
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
/// The Money page (<c>/money</c>; money design §9.1): the mode and settings, and while money is on the totals, the
/// self-check, the grid-wide freeze, refunds, moving fees and finding a person. Every staff member may look; only admins
/// and the owner may change anything (each POST checks), and every change is audited.
/// </summary>
public sealed class IndexModel(IGridController grid, MoneyGate gate, IMoneyBooks books, MoneyStatus status, AuditLog audit, IRobustClient robust,
    IPeopleDirectory people, IVisitorDirectory visitors, MoneyPairing pairing, RegionPins pins, TimeProvider time)
    : PageModel
{
    public IReadOnlyList<MoneyMode> Offered { get; private set; } = [];

    /// <summary>Gloebit's section: shown when the gate lets Gloebit run (money plan 4).</summary>
    public bool ShowGloebit { get; private set; }
    public bool GloebitMode { get; private set; }

    /// <summary>Gloebit calls back into the regions: say so where nobody outside can reach them.</summary>
    public bool GloebitUnreachable { get; private set; }

    [BindProperty] public bool ConfirmSwitch { get; set; }
    public MoneySettings Saved { get; private set; } = new();

    /// <summary>What the settings form shows: what was posted when it was refused, so nothing has to be chosen again.</summary>
    public MoneySettings Form => _refused ?? Saved;

    private MoneySettings? _refused;
    public bool On { get; private set; }
    public bool CanChange { get; private set; }
    public MoneyTotals? Totals { get; private set; }
    public (bool Frozen, string Reason) Frozen { get; private set; }
    public (DateTimeOffset At, SelfCheckResult Result)? LastCheck { get; private set; }
    public bool DatabaseDown { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    [BindProperty] public string? Reason { get; set; }

    /// <summary>The Find a person box: part of a name.</summary>
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public IReadOnlyList<PersonRow> Found { get; private set; } = [];
    public IReadOnlyList<VisitorInfo> FoundVisitors { get; private set; } = [];

    /// <summary>The Region Servers section: a Grid Asset Server whose ledger runs (money plan 3).</summary>
    public bool ShowServers { get; private set; }
    public IReadOnlyList<PairedServer> Servers { get; private set; } = [];
    public DateTimeOffset? PairingUntil { get; private set; }

    /// <summary>A pairing code just made: shown once, on this answer only.</summary>
    public string? NewCode { get; private set; }

    /// <summary>The most visitors Find a person shows.</summary>
    public const int MaxVisitorsFound = 50;

    public static string VisitorLabel(VisitorInfo v) =>
        $"{v.Name} (visitor from {(Uri.TryCreate(v.Home, UriKind.Absolute, out var u) ? u.Host : v.Home)})";
    public bool SearchDown { get; private set; }

    public static string N(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public Task OnGetAsync(CancellationToken ct) => ShowAsync(ct);

    public async Task<IActionResult> OnPostSettingsAsync(MoneyMode mode, string? currencySymbol, string? currencyName, long startingBalancePlay,
        long startingBalanceReal, long weeklyAllowance, long uploadFee, long classifiedFee, long dailyPayoutLimit, long maxPayment, bool scriptPayouts,
        bool visitorWallets, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        var before = grid.CurrentSettings().Money;
        var next = before with
        {
            Mode = mode, CurrencySymbol = currencySymbol?.Trim() ?? "", CurrencyName = currencyName?.Trim() ?? "",
            StartingBalancePlay = startingBalancePlay, StartingBalanceReal = startingBalanceReal, WeeklyAllowance = weeklyAllowance,
            UploadFee = uploadFee, ClassifiedFee = classifiedFee, DailyPayoutLimit = dailyPayoutLimit, MaxPayment = maxPayment,
            ScriptPayouts = scriptPayouts, VisitorWallets = visitorWallets,
        };
        // The form may only be posted with values the page itself would accept; anything else is refused whole.
        if (!ModelState.IsValid) Error = "Every number must be a whole number.";
        else Error = MoneyPageRules.Problem(next, MoneyPageRules.Offered(gate));
        // Coins and Gloebit are separate books (design §10): the owner confirms that before switching.
        // A money database is made the first time coins run (money design §7.1).
        var coinsUsed = grid.CurrentSettings().Db.MoneyPassword != "";
        if (Error is null && MoneyPageRules.CrossesGloebit(before.Mode, next.Mode, coinsUsed, before.Gloebit is { Key.Length: > 0 }) && !ConfirmSwitch)
            Error = MoneyPageRules.SwitchWarning + " Tick the box to switch.";
        if (Error is not null) _refused = next;
        if (Error is null)
        {
            // Only the fields above change: the program keys, and anything another page saved meanwhile, stay.
            grid.UpdateSettings(s => s with
            {
                Money = s.Money with
                {
                    Mode = next.Mode, CurrencySymbol = next.CurrencySymbol, CurrencyName = next.CurrencyName,
                    StartingBalancePlay = next.StartingBalancePlay, StartingBalanceReal = next.StartingBalanceReal, WeeklyAllowance = next.WeeklyAllowance,
                    UploadFee = next.UploadFee, ClassifiedFee = next.ClassifiedFee, DailyPayoutLimit = next.DailyPayoutLimit,
                    MaxPayment = next.MaxPayment, ScriptPayouts = next.ScriptPayouts, VisitorWallets = next.VisitorWallets,
                },
            }, writeConfig: true);
            audit.Write(User, Ip, "money-settings", "money", before.Mode == next.Mode ? "ok" : "mode " + next.Mode);
            Notice = before.Mode == next.Mode ? "Saved." : "Saved. " + MoneyPageRules.RestartWorld;
        }
        await ShowAsync(ct);
        return Page();
    }

    /// <summary>This computer's Gloebit app. An empty secret keeps the stored one; nothing posted is ever echoed or audited.</summary>
    public async Task<IActionResult> OnPostGloebitAsync(string? environment, string? key, string? secret, string? ownerName, string? ownerEmail,
        CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!gate.Approved(MoneyMode.Gloebit))
        {
            Error = "Gloebit becomes available once the money safety review is approved.";
            await ShowAsync(ct);
            return Page();
        }
        (var app, Error) = MoneyPageRules.GloebitFromForm(grid.CurrentSettings().Money.Gloebit, environment, key, secret, ownerName, ownerEmail);
        if (Error is null)
        {
            grid.UpdateSettings(s => s with { Money = s.Money with { Gloebit = app } }, writeConfig: true);
            Notice = "Saved this computer's Gloebit app. " + MoneyPageRules.RestartWorld;
        }
        audit.Write(User, Ip, "money-gloebit", "money", Error is null ? "ok" : "refused");
        await ShowAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPairingCodeAsync(CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardServersAsync(ct)) return Page();
        var (code, _) = pairing.Issue();
        NewCode = PairingCode.Show(code);
        audit.Write(User, Ip, "money-pairing-code", "money", "ok");
        await ShowAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPairingCancelAsync(CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardServersAsync(ct)) return Page();
        pairing.Cancel();
        audit.Write(User, Ip, "money-pairing-cancel", "money", "ok");
        Notice = "The pairing code was cancelled.";
        await ShowAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveServerAsync(Guid server, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardServersAsync(ct)) return Page();
        var gone = grid.CurrentSettings().Money.PairedServers?.FirstOrDefault(p => p.Id == server);
        if (gone is null)
        {
            Error = "That Region Server isn't paired.";
        }
        else
        {
            grid.UpdateSettings(s => s with { Money = s.Money with { PairedServers = (s.Money.PairedServers ?? []).Where(p => p.Id != server).ToList() } },
                writeConfig: false);
            audit.Write(User, Ip, "money-pairing-remove", LogText.Clean(gone.Host), "ok");
            Notice = "Removed. Its regions can't take payments until it's paired again.";
            try
            {
                await pins.RemoveServerAsync(server, ct);
            }
            catch (MoneyUnavailableException)
            {
                // The key is gone already, which ends its coverage; leftover pins only make its regions "moved" later.
                Notice += " The money database didn't answer, so its region pins were kept.";
            }
        }
        await ShowAsync(ct);
        return Page();
    }

    private async Task<bool> GuardServersAsync(CancellationToken ct)
    {
        if (gate.ServiceHere && grid.CurrentSettings().Plan == OwnerPlan.GridAsset) return true;
        Error = "Region Servers pair only with a Grid Asset Server whose money is on.";
        await ShowAsync(ct);
        return false;
    }

    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardOnAsync(ct)) return Page();
        try
        {
            var result = await books.SelfCheckAsync(ct);
            status.LastCheck = (time.GetUtcNow(), result);
            audit.Write(User, Ip, "money-self-check", "money", result.Ok ? "ok" : "difference");
            if (result.Ok) Notice = "The books add up.";
            else Error = "The self-check found a difference and froze all payments.";
        }
        catch (MoneyUnavailableException)
        {
            audit.Write(User, Ip, "money-self-check", "money", "unavailable");
            Error = MoneyPageRules.Words(MoneyOutcome.Unavailable);
        }
        await ShowAsync(ct);
        return Page();
    }

    public Task<IActionResult> OnPostFreezeAsync(CancellationToken ct) => FreezeAsync(true, ct);

    public Task<IActionResult> OnPostUnfreezeAsync(CancellationToken ct) => FreezeAsync(false, ct);

    private async Task<IActionResult> FreezeAsync(bool frozen, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardOnAsync(ct)) return Page();
        if (CleanReason() is not { } reason)
        {
            Error = "Say why.";
        }
        else
        {
            try
            {
                await books.SetFrozenAsync(frozen, reason, ct);
                audit.Write(User, Ip, frozen ? "money-freeze-all" : "money-unfreeze-all", "money", "ok");
                Notice = frozen ? "All payments are frozen." : "Payments are running again.";
            }
            catch (MoneyUnavailableException)
            {
                audit.Write(User, Ip, frozen ? "money-freeze-all" : "money-unfreeze-all", "money", "unavailable");
                Error = MoneyPageRules.Words(MoneyOutcome.Unavailable);
            }
        }
        await ShowAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRefundAsync(long entryId, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardOnAsync(ct)) return Page();
        if (CleanReason() is not { } reason || entryId <= 0)
        {
            Error = "Give the entry number and say why.";
        }
        else
        {
            var r = await books.RefundAsync(entryId, Staff(reason), ct);
            audit.Write(User, Ip, "money-refund", entryId.ToString(System.Globalization.CultureInfo.InvariantCulture), MoneyPageRules.Code(r.Outcome));
            Done(r.Outcome);
        }
        await ShowAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostMoveFeesAsync(string? name, long amount, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardOnAsync(ct)) return Page();
        var parts = (name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var account = parts.Length == 2 ? await robust.FindAccountAsync(parts[0], parts[1], ct) : null;
        if (CleanReason() is not { } reason || amount <= 0)
        {
            Error = "Give an amount and say why.";
        }
        else if (account is null)
        {
            Error = "No resident has that name.";
        }
        else
        {
            var r = await books.MoveFeesAsync(AccountRef.Resident(account.PrincipalId), amount, Staff(reason), ct);
            audit.Write(User, Ip, "money-move-fees", LogText.Clean(account.FullName()),
                MoneyPageRules.Code(r.Outcome) + " " + amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Done(r.Outcome);
        }
        await ShowAsync(ct);
        return Page();
    }

    private bool Admin => StaffSignIn.RoleOf(User) >= StaffRole.Admin;

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private StaffAction Staff(string reason) => new(User.Identity?.Name ?? "staff", reason);

    private string? CleanReason() => string.IsNullOrWhiteSpace(Reason) ? null : LogText.Clean(Reason.Trim(), MoneyPageRules.MaxReason);

    private void Done(MoneyOutcome o)
    {
        if (o == MoneyOutcome.Ok) Notice = MoneyPageRules.Words(o);
        else Error = MoneyPageRules.Words(o);
    }

    /// <summary>The ledger's actions exist only while money is on.</summary>
    private async Task<bool> GuardOnAsync(CancellationToken ct)
    {
        if (gate.ServiceHere) return true;
        Error = "Money is off.";
        await ShowAsync(ct);
        return false;
    }

    private async Task ShowAsync(CancellationToken ct)
    {
        Saved = grid.CurrentSettings().Money;
        Offered = MoneyPageRules.Offered(gate);
        ShowGloebit = gate.Approved(MoneyMode.Gloebit);
        GloebitMode = gate.Effective() == MoneyMode.Gloebit;
        var current = grid.CurrentSettings();
        GloebitUnreachable = GloebitMode && (current.Plan == OwnerPlan.Local
            || current.Plan == OwnerPlan.GridAsset && !Core.Config.HostAddress.IsPublic(current.Hostname));
        On = gate.ServiceHere;
        CanChange = Admin;
        if (!On) return;
        LastCheck = status.LastCheck;
        ShowServers = grid.CurrentSettings().Plan == OwnerPlan.GridAsset;
        Servers = Saved.PairedServers ?? [];
        PairingUntil = pairing.PendingUntil;
        if (!string.IsNullOrWhiteSpace(Q))
            try
            {
                // The first page of residents (the directory's page size); the staff level is unused for Everyone.
                Found = (await people.SearchAsync(Q.Trim(), PeopleFilter.Everyone, [], staffLevel: 0, page: 1, ct)).Rows;
                FoundVisitors = await visitors.SearchAsync(Q.Trim(), MaxVisitorsFound, ct);
            }
            catch (Exception e) when (e is PeopleDirectoryUnavailableException or MoneyUnavailableException)
            {
                SearchDown = true;
            }
        try
        {
            Totals = await books.TotalsAsync(ct);
            Frozen = await books.FrozenAsync(ct);
        }
        catch (MoneyUnavailableException)
        {
            DatabaseDown = true;
        }
    }
}
