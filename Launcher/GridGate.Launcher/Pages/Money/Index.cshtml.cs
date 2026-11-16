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
using RegionState = GridGate.Launcher.Core.Money.RegionState;
using RegionStatus = GridGate.Launcher.Core.Money.RegionStatus;

namespace GridGate.Launcher.Pages.Money;

/// <summary>One region program or Region Server in the Limits section: its key, its name, and its limits now (null when unreadable).</summary>
public sealed record KeyLimitRow(string KeyId, string Name, LimitView? View);

/// <summary>
/// One open alert as the Money page shows it (verified-regions design §8.8): what happened, the person affected (named,
/// with their page when they're a resident), and the paired server a warning may offer to remove.
/// </summary>
public sealed record AlertRow(MoneyAlert Alert, string Text, string? Person, Guid? PersonPage, Guid? RemovableServer);

/// <summary>
/// The Money page (<c>/money</c>; money design §9.1): the mode and settings, visitor money and listed grids, and while money
/// is on the totals, the self-check, the grid-wide freeze, refunds, moving fees, finding a person, and (verified-regions
/// design §8.12) the regions and whether each is verified, each region program's and Region Server's own limits, and the
/// open alerts. Every staff member may look; only admins and the owner may change anything, and only the owner may set a
/// region program's or server's own limits or settle a moved region (each POST checks). Every change is audited.
/// </summary>
public sealed class IndexModel(IGridController grid, MoneyGate gate, IMoneyBooks books, MoneyStatus status, AuditLog audit, IRobustClient robust,
    IPeopleDirectory people, IVisitorDirectory visitors, MoneyPairing pairing, RegionPins pins, TimeProvider time, IMoneyAlerts alerts,
    IMoneyRegions regions)
    : PageModel
{
    /// <summary>The settings the page was drawn from: names of servers, programs and regions.</summary>
    public LauncherSettings Current { get; private set; } = null!;

    /// <summary>Only the owner sets a program's or server's own limits and settles a moved region.</summary>
    public bool IsOwner { get; private set; }

    /// <summary>Every region Robust lists, with whether money works there (verified-regions design §8.2).</summary>
    public IReadOnlyList<RegionState> Regions { get; private set; } = [];
    public bool RegionsDown { get; private set; }

    /// <summary>Each key's last health call, for the Region Servers list.</summary>
    public IReadOnlyDictionary<string, KeyHeard> KeysHeard { get; private set; } = new Dictionary<string, KeyHeard>();

    public IReadOnlyList<KeyLimitRow> KeyLimits { get; private set; } = [];
    public IReadOnlyList<AlertRow> Alerts { get; private set; } = [];
    public bool AlertsDown { get; private set; }

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
        long? dailySpendLimit, long? dailyKeySpendLimit, long? dailyKeyPayoutLimit, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        var before = grid.CurrentSettings().Money;
        // Visitor money has its own section (OnPostVisitorsAsync): this form leaves it as it is.
        var next = before with
        {
            Mode = mode, CurrencySymbol = currencySymbol?.Trim() ?? "", CurrencyName = currencyName?.Trim() ?? "",
            StartingBalancePlay = startingBalancePlay, StartingBalanceReal = startingBalanceReal, WeeklyAllowance = weeklyAllowance,
            UploadFee = uploadFee, ClassifiedFee = classifiedFee, DailyPayoutLimit = dailyPayoutLimit, MaxPayment = maxPayment,
            ScriptPayouts = scriptPayouts, DailySpendLimit = dailySpendLimit ?? before.DailySpendLimit,
            DailyKeySpendLimit = dailyKeySpendLimit ?? before.DailyKeySpendLimit, DailyKeyPayoutLimit = dailyKeyPayoutLimit ?? before.DailyKeyPayoutLimit,
        };
        // The form may only be posted with values the page itself would accept; anything else is refused whole.
        if (!ModelState.IsValid) Error = "Every number must be a whole number.";
        // A form from before the daily limits existed (a tab left open across an update) has none: a missing limit read as 0
        // would stop every payment, so the whole form is refused instead.
        else if (dailySpendLimit is null || dailyKeySpendLimit is null || dailyKeyPayoutLimit is null)
            Error = "The daily limits are missing from the form. Reload the page and save again.";
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
                    MaxPayment = next.MaxPayment, ScriptPayouts = next.ScriptPayouts, DailySpendLimit = next.DailySpendLimit,
                    DailyKeySpendLimit = next.DailyKeySpendLimit, DailyKeyPayoutLimit = next.DailyKeyPayoutLimit,
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

    /// <summary>Which hypergrid visitors may pay and be paid (verified-regions design §8.6).</summary>
    public async Task<IActionResult> OnPostVisitorsAsync(VisitorMoney visitors, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!ModelState.IsValid || !Enum.IsDefined(visitors))
        {
            Error = "Choose who may use money: no visitors, visitors from grids you list, or visitors from all grids.";
        }
        else
        {
            grid.UpdateSettings(s => s with { Money = s.Money with { Visitors = visitors } }, writeConfig: false);
            audit.Write(User, Ip, "money-visitors", "money", visitors.ToString().ToLowerInvariant());
            Notice = "Saved.";
        }
        await ShowAsync(ct);
        return Page();
    }

    /// <summary>Lists a grid whose visitors may use money (§8.6), by its home address, stored as the ledger stores homes.</summary>
    public async Task<IActionResult> OnPostAddGridAsync(string? address, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (MoneyPageRules.GridAddress(address) is not { } home)
        {
            Error = "Give the grid's home address, like http://hg.friendgrid.net:8002.";
        }
        else if (grid.CurrentSettings().Money.GridListed(home))
        {
            Notice = "That grid is already listed.";
        }
        else
        {
            grid.UpdateSettings(s => s.Money.GridListed(home) ? s : s with { Money = s.Money with { ListedGrids = [.. s.Money.ListedGrids ?? [], home] } },
                writeConfig: false);
            audit.Write(User, Ip, "money-grid-add", LogText.Clean(home), "ok");
            Notice = "Listed.";
        }
        await ShowAsync(ct);
        return Page();
    }

    /// <summary>Takes a grid off the list (§8.6): its visitors' wallets stay, frozen in place, until it is listed again.</summary>
    public async Task<IActionResult> OnPostRemoveGridAsync(string address, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        var wanted = MoneyHomes.Normal(address);
        if (string.IsNullOrEmpty(wanted) || !grid.CurrentSettings().Money.GridListed(wanted))
        {
            Error = "That grid isn't listed.";
        }
        else
        {
            grid.UpdateSettings(s => s with { Money = s.Money with { ListedGrids = (s.Money.ListedGrids ?? []).Where(g => MoneyHomes.Normal(g) != wanted).ToList() } },
                writeConfig: false);
            audit.Write(User, Ip, "money-grid-remove", LogText.Clean(wanted), "ok");
            Notice = "Removed. Its visitors' coins are kept, frozen, and work again if you list the grid again.";
        }
        await ShowAsync(ct);
        return Page();
    }

    /// <summary>
    /// A region program's or Region Server's own daily limits (§8.7): any amount, or empty for the grid default (both empty
    /// removes them). Owner only, with a reason; the audit line is the only record of a removal.
    /// </summary>
    public async Task<IActionResult> OnPostKeyLimitsAsync(string keyId, long? dailySpend, long? dailyPayout, CancellationToken ct)
    {
        if (!Owner) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardOnAsync(ct)) return Page();
        var s = grid.CurrentSettings();
        if (!KeyIds(s).Contains(keyId))
        {
            Error = "That isn't a region program or Region Server here.";
        }
        else if (CleanReason() is not { } reason)
        {
            Error = "Say why.";
        }
        else if (!ModelState.IsValid)
        {
            Error = "Each limit must be a whole number, or empty for the grid default.";
        }
        else if (MoneyPageRules.LimitProblem(dailySpend, dailyPayout) is { } problem)
        {
            Error = problem;
        }
        else
        {
            var o = await books.SetLimitsAsync(LimitTarget.ForKey(keyId), dailySpend, dailyPayout, null, Staff(reason), ct);
            audit.Write(User, Ip, "money-key-limits", LogText.Clean(MoneyVerifyText.KeyName(keyId, s)),
                MoneyPageRules.Code(o) + " " + MoneyPageRules.LimitsText(dailySpend, dailyPayout));
            if (o == MoneyOutcome.Ok) Notice = dailySpend is null && dailyPayout is null ? "Back to the grid defaults." : "Saved.";
            else Error = MoneyPageRules.Words(o);
        }
        await ShowAsync(ct);
        return Page();
    }

    /// <summary>
    /// Approves a moved region (§8.3): it is pinned to the Region Server Robust now lists it at, and its move notice is
    /// cleared. Owner only; audited.
    /// </summary>
    public async Task<IActionResult> OnPostApproveMoveAsync(Guid region, Guid server, CancellationToken ct)
    {
        if (!Owner) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardServersAsync(ct)) return Page();
        var s = grid.CurrentSettings();
        try
        {
            var to = s.Money.PairedServers?.FirstOrDefault(p => p.Id == server);
            var moved = to is null ? null : await MovedAsync(s, region, ct);
            if (to is null || moved is null || moved.KeyId != MoneyVerifyText.KeyOf(to))
            {
                Error = "That region isn't waiting for a move to that Region Server any more.";
            }
            else
            {
                await regions.ApproveMoveAsync(region, server, ct);
                // §8.3: "Approving re-pins it to A, with an audit entry": written as soon as the pin moved, whatever follows.
                audit.Write(User, Ip, "money-region-move", LogText.Clean(moved.Name), "approved");
                Notice = $"Approved: {moved.Name} now belongs to the {MoneyVerifyText.ServerName(to)}. Money works there once its module checks in.";
                try
                {
                    await ClearMovedAsync(region, ct);
                }
                catch (MoneyUnavailableException)
                {
                    Notice += " Its move notice couldn't be cleared: clear it under Alerts later.";
                }
            }
        }
        catch (MoneyUnavailableException)
        {
            Error = MoneyPageRules.Words(MoneyOutcome.Unavailable);
        }
        await ShowAsync(ct);
        return Page();
    }

    /// <summary>
    /// Keeps a moved region off (§8.3, Ruling 23): no pin changes, so money comes back if the server it belongs to registers
    /// it again. Clears its move notice, which comes back at most once a day while the other server lists it. Owner only; audited.
    /// </summary>
    public async Task<IActionResult> OnPostKeepOffAsync(Guid region, CancellationToken ct)
    {
        if (!Owner) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardServersAsync(ct)) return Page();
        try
        {
            if (await MovedAsync(grid.CurrentSettings(), region, ct) is not { } moved)
            {
                Error = "That region isn't waiting for a move any more.";
            }
            else
            {
                await ClearMovedAsync(region, ct);
                audit.Write(User, Ip, "money-region-move", LogText.Clean(moved.Name), "kept off");
                Notice = $"Kept off: money stays off in {moved.Name}. It comes back if the server it belongs to registers it again.";
            }
        }
        catch (MoneyUnavailableException)
        {
            Error = MoneyPageRules.Words(MoneyOutcome.Unavailable);
        }
        await ShowAsync(ct);
        return Page();
    }

    /// <summary>Clears an alert once it has been dealt with (§8.8). The alert keeps who cleared it and when.</summary>
    public async Task<IActionResult> OnPostClearAlertAsync(long id, CancellationToken ct)
    {
        if (!Admin) return StatusCode(StatusCodes.Status403Forbidden);
        if (!await GuardOnAsync(ct)) return Page();
        var target = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            await alerts.ClearAsync(id, StaffName, ct);
            audit.Write(User, Ip, "money-alert-clear", target, "ok");
            Notice = "Cleared.";
        }
        catch (MoneyUnavailableException)
        {
            audit.Write(User, Ip, "money-alert-clear", target, "unavailable");
            Error = MoneyPageRules.Words(MoneyOutcome.Unavailable);
        }
        await ShowAsync(ct);
        return Page();
    }

    /// <summary><paramref name="region"/>'s state when Robust lists it as moved, else null.</summary>
    private async Task<RegionState?> MovedAsync(LauncherSettings s, Guid region, CancellationToken ct) =>
        (await regions.AllAsync(s, ct)).FirstOrDefault(r => r.RegionId == region && r.Status == RegionStatus.Moved);

    private async Task ClearMovedAsync(Guid region, CancellationToken ct)
    {
        foreach (var a in (await alerts.OpenAsync(ct)).Where(a => a.Kind == AlertKind.RegionMoved && a.RegionId == region))
            await alerts.ClearAsync(a.Id, StaffName, ct);
    }

    /// <summary>Every key here: each region program's, then each paired Region Server's (§8.7: "per key" means every key).</summary>
    private static List<string> KeyIds(LauncherSettings s) =>
        [.. s.Programs.Select(p => p.Instance), .. (s.Money.PairedServers ?? []).Select(MoneyVerifyText.KeyOf)];

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

    private bool Owner => StaffSignIn.RoleOf(User) == StaffRole.Owner;

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string StaffName => User.Identity?.Name ?? "staff";

    private StaffAction Staff(string reason) => new(StaffName, reason);

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
        IsOwner = Owner;
        Current = current;
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
            KeyLimits = await KeyLimitRowsAsync(current, ct);
        }
        catch (MoneyUnavailableException)
        {
            DatabaseDown = true;
        }
        try
        {
            Regions = await regions.AllAsync(current, ct);
            if (ShowServers) KeysHeard = await regions.KeysAsync(ct);
        }
        catch (MoneyUnavailableException)
        {
            RegionsDown = true;
        }
        try
        {
            Alerts = await AlertRowsAsync(current, ct);
        }
        catch (MoneyUnavailableException)
        {
            AlertsDown = true;
        }
    }

    private async Task<List<KeyLimitRow>> KeyLimitRowsAsync(LauncherSettings s, CancellationToken ct)
    {
        var rows = new List<KeyLimitRow>();
        foreach (var key in KeyIds(s))
            rows.Add(new KeyLimitRow(key, MoneyVerifyText.KeyName(key, s), await books.LimitsAsync(LimitTarget.ForKey(key), ct)));
        return rows;
    }

    /// <summary>
    /// The open alerts in words: each names its region (from Robust's list, read first) or server, and the person affected.
    /// Only a warning about a server that is still paired offers to remove it, and for an arrival it wasn't handed, only one
    /// raised by that server's own repeated claims: in a disagreement between two servers, either may be the honest one.
    /// </summary>
    private async Task<List<AlertRow>> AlertRowsAsync(LauncherSettings s, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string?>();
        var rows = new List<AlertRow>();
        foreach (var a in await alerts.OpenAsync(ct))
        {
            var region = a.RegionId is not { } id ? "a region"
                : Regions.FirstOrDefault(r => r.RegionId == id)?.Name ?? s.Regions.FirstOrDefault(r => r.Uuid == id)?.Name ?? $"region {id}";
            string? person = null;
            if (a.AgentId is { } agent && !names.TryGetValue(agent, out person))
                names[agent] = person = await ResidentNameAsync(agent, ct);
            var removable = a.Level == AlertLevel.Warning && (a.Kind != AlertKind.UnhandedArrival || a.Repeated);
            var server = removable ? s.Money.PairedServers?.FirstOrDefault(p => MoneyVerifyText.KeyOf(p) == a.KeyId)?.Id : null;
            // A visitor isn't a resident, and Robust may be down: either way the alert still says whose avatar it was.
            var who = person ?? (a.AgentId is { } agentId ? $"avatar {agentId}" : null);
            var other = a.OtherKeyId is { } otherKey ? MoneyVerifyText.KeyName(otherKey, s) : null;
            rows.Add(new AlertRow(a, MoneyVerifyText.Alert(a, region, MoneyVerifyText.KeyName(a.KeyId, s), other, who),
                who, person is null ? null : a.AgentId, server));
        }
        return rows;
    }

    /// <summary>
    /// A resident's name from Robust; null for anyone else, or when Robust can't answer, so an outage there can't take down
    /// the Money page and its Freeze button. Only the request's own cancellation goes on.
    /// </summary>
    private async Task<string?> ResidentNameAsync(Guid agent, CancellationToken ct)
    {
        try
        {
            return (await robust.FindAccountByIdAsync(agent, ct))?.FullName();
        }
        catch (Exception e) when ((e is HttpRequestException or InvalidDataException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
