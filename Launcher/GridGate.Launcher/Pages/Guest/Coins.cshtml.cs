using System.Globalization;
using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Money;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>One line of My coins, ready to show: what happened (with the object's name), and the other side as a name, a visitor's grid, or nothing.</summary>
public sealed record CoinsLine(DateTimeOffset At, string What, string Who, long Amount);

/// <summary>
/// My coins (<c>/guest/coins</c>; money design §9.5): the resident's balance, the newest 50 lines, and the "my scripts may
/// pay out" switch, which is off until they turn it on; and one line when money is paused for them (verified-regions
/// design §8.12). Not there while money is off.
/// </summary>
[MoneyPagesOnly]
public sealed class CoinsModel(IMoneyBooks books, Func<LauncherSettings> settings, IRobustClient robust, AuditLog audit,
    IStringLocalizer<ResidentText> t, IMoneyRegions regions) : PageModel
{
    /// <summary>Money is paused for them: where they are now isn't verified. On another grid they have no location here (Ruling 24).</summary>
    /// <summary>The text key of the line saying money is paused for them, and why; null when it isn't.</summary>
    public string? Paused { get; private set; }

    public const int HistoryLines = 50;

    // Lines that move coins in or out of Holding (a purchase on its way): the other side is never shown (plan 1 deferred finding).
    private static readonly HashSet<string> HoldTypes = [EntryType.Hold, EntryType.HoldCompleted, EntryType.HoldReturned];

    public string Symbol { get; private set; } = "";
    public long? Balance { get; private set; }
    public IReadOnlyList<CoinsLine> Lines { get; private set; } = [];
    public bool PayoutsAllowed { get; private set; }
    public bool ScriptPayoutsOnGrid { get; private set; }
    public long DailyLimit { get; private set; }
    public bool Unavailable { get; private set; }
    public bool Saved { get; private set; }

    public string Amount(long n) => Symbol + n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Signed(long n) => (n > 0 ? "+" : "") + n.ToString("N0", CultureInfo.CurrentCulture);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (GuestSignIn.Read(User) is not { } guest) return Challenge();
        await LoadAsync(guest.Principal, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPayoutsAsync(bool on, CancellationToken ct)
    {
        if (GuestSignIn.Read(User) is not { } guest) return Challenge();
        if (settings().Money.ScriptPayouts)
        {
            var r = await books.SetPayoutsAllowedAsync(guest.Principal, on, ct);
            var name = LogText.Clean(User.Identity?.Name, 64);
            audit.Write(new AuditEntry(name, guest.Principal, AuthSchemes.GuestRole, HttpContext.Connection.RemoteIpAddress?.ToString(), "money-payouts", name,
                r.Outcome == MoneyOutcome.Ok ? (on ? "on" : "off") : System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(r.Outcome.ToString())));
            Saved = r.Outcome == MoneyOutcome.Ok;
        }
        await LoadAsync(guest.Principal, ct);
        return Page();
    }

    private async Task LoadAsync(Guid principal, CancellationToken ct)
    {
        var m = settings().Money;
        Symbol = m.CurrencySymbol;
        ScriptPayoutsOnGrid = m.ScriptPayouts;
        DailyLimit = m.DailyPayoutLimit;
        var me = AccountRef.Resident(principal);
        try
        {
            // The first visit makes the wallet, with the starting balance.
            var balance = await books.BalanceAsync(me, ct);
            if (balance.Outcome != MoneyOutcome.Ok || balance.FromBalance is not { } b)
            {
                Unavailable = true;
                return;
            }
            Balance = b;
            PayoutsAllowed = (await books.AccountFlagsAsync(me, ct))?.PayoutsAllowed ?? false;
            var names = new Dictionary<Guid, string>();
            var lines = new List<CoinsLine>();
            foreach (var line in await books.HistoryAsync(me, HistoryLines, ct))
                lines.Add(new CoinsLine(line.At, What(line), HoldTypes.Contains(line.Type) ? "" : await WhoAsync(line.Other, names, ct), line.Amount));
            Lines = lines;
        }
        catch (MoneyUnavailableException)
        {
            Unavailable = true;
            Balance = null;
            Lines = [];
        }
        try
        {
            // Their newest logged-in session (Ruling 3); nothing when they aren't in the world.
            Paused = (await regions.WhereAgentAsync(principal, ct))?.State switch
            {
                LocationState.Unverified => "MyCoins.MoneyPaused",
                LocationState.Handed => "MyCoins.MoneyPausedMoved",
                _ => null,
            };
        }
        catch (MoneyUnavailableException)
        {
            // Only this line depends on it: the rest of the page stands.
        }
    }

    /// <summary>
    /// What a line was, in words. A completed hold is the seller's side: a sale, of land when the hold was for land. The
    /// buyer's own hold line stays for good, so its words never say it is still waiting.
    /// </summary>
    private string What(HistoryLine line)
    {
        var type = line.Type == EntryType.HoldCompleted && line.Reason == EntryType.LandSale ? EntryType.LandSale : line.Type;
        string words = t["MyCoins.Type." + type];
        return string.IsNullOrWhiteSpace(line.ObjectName) ? words : t["MyCoins.WhatWithObject", words, line.ObjectName];
    }

    /// <summary>The other side of a line: a resident's name, "Visitor from host", or nothing for the grid's own accounts.</summary>
    private async Task<string> WhoAsync(AccountRef? other, Dictionary<Guid, string> names, CancellationToken ct)
    {
        if (other is not { } o) return "";
        if (!o.IsResident)
        {
            var host = Uri.TryCreate(o.Home, UriKind.Absolute, out var u) ? u.Host : LogText.Clean(o.Home);
            return t["MyCoins.Visitor", host];
        }
        if (!names.TryGetValue(o.AgentId, out var name))
            names[o.AgentId] = name = (await robust.FindAccountByIdAsync(o.AgentId, ct))?.FullName() ?? "";
        return name;
    }
}
