using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Advanced;

/// <summary>
/// A Region Server's money (<c>/advanced/money</c>; money design §7.7, money plan 3): the mode its Grid Asset Server
/// publishes, and pairing with that server's money service by a one-time code. Region Servers only; admins only (the
/// Advanced folder's rule). The code and the key are never shown or audited.
/// </summary>
public sealed class MoneyModel(IGridController grid, MoneyGate gate, IMoneyPairClient pair, AuditLog audit) : PageModel
{
    public enum PairState { NotPaired, Paired, CertificateChanged }

    [BindProperty] public string? Code { get; set; }

    public MoneyMode GridMode { get; private set; }
    public PairState State { get; private set; }
    public DateTimeOffset? PairedAt { get; private set; }
    public bool CanPair { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public string ModeText => GridMode switch
    {
        MoneyMode.Play => "The grid's money is Play coins.",
        MoneyMode.Real => "The grid's money is real coins.",
        MoneyMode.Gloebit => "The grid uses Gloebit.",
        _ => "The grid's money is off.",
    };

    public IActionResult OnGet() => Show() ? Page() : NotFound();

    public async Task<IActionResult> OnPostPairAsync(CancellationToken ct)
    {
        if (!Show()) return NotFound();
        if (!CanPair)
        {
            Error = "There's nothing to pair while the grid's money isn't GridGate coins.";
            return Page();
        }
        var outcome = await pair.PairAsync(grid.CurrentSettings(), Code ?? "", ct);
        // The join may have been pointed at another Grid Asset Server while pairing ran (another tab): then neither the
        // pairing nor what was read belongs to it.
        if (outcome.Pairing is { } moved && !string.Equals(grid.CurrentSettings().Join?.Host, moved.GridHost, StringComparison.OrdinalIgnoreCase))
        {
            audit.Write(User, Ip, "money-pair", "money", "refused");
            Error = "The Grid Asset Server changed while pairing. Pair again.";
            Show();
            return Page();
        }
        if (outcome.Pairing is { } pairing)
        {
            // With the grid information pairing just read: the saved join is only refreshed when the grid service starts.
            grid.UpdateSettings(s => s with
            {
                Money = s.Money with { Pairing = pairing },
                Join = outcome.Grid is { } fresh && s.Join is { } current && current.Host == fresh.Host && current.PublicPort == fresh.PublicPort
                    ? fresh with { Host = current.Host, PublicPort = current.PublicPort }
                    : s.Join,
                GridName = outcome.Grid is { } named && s.Join is { } here && here.Host == named.Host ? named.GridName : s.GridName,
            }, writeConfig: true);
            audit.Write(User, Ip, "money-pair", "money", "ok");
            Notice = "Paired. Restart the regions to start taking payments.";
        }
        else
        {
            audit.Write(User, Ip, "money-pair", "money", "refused");
            Error = outcome.Error;
        }
        Show();
        return Page();
    }

    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct)
    {
        if (!Show()) return NotFound();
        var health = await pair.CheckAsync(grid.CurrentSettings(), ct);
        var words = health switch
        {
            PairingHealth.Ok => "The grid's money service accepts this server's key.",
            PairingHealth.Refused => "The grid no longer accepts this server's key. Pair again.",
            PairingHealth.CertificateChanged => "The grid's money certificate changed: pair again.",
            PairingHealth.NotPaired => "This server isn't paired.",
            PairingHealth.SomeRegions => "The grid sees only some of this server's regions. Start the others; if they are running, pair again so the grid knows this server's ports.",
            PairingHealth.NoRegions => "The grid accepts this server's key, but sees none of its regions yet. Start them, and check that this computer's address and first region port haven't changed since pairing.",
            _ => "Couldn't reach the grid's money service. Is the Grid Asset Server running?",
        };
        Show();
        if (health == PairingHealth.Ok) Notice = words;
        else Error = words;
        return Page();
    }

    public IActionResult OnPostUnpair()
    {
        if (!Show()) return NotFound();
        grid.UpdateSettings(s => s with { Money = s.Money with { Pairing = null } }, writeConfig: true);
        audit.Write(User, Ip, "money-unpair", "money", "ok");
        Notice = "Unpaired. Restart the regions; remove it on the Grid Asset Server's Money page too, so its key ends there.";
        Show();
        return Page();
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Fills the page from the settings; false on any plan but Region Server.</summary>
    private bool Show()
    {
        var s = grid.CurrentSettings();
        if (s.Plan != OwnerPlan.RegionServer) return false;
        GridMode = gate.Effective();
        // The saved join can be older than the grid's money (it's refreshed when the grid service starts); pairing reads
        // the grid fresh and says when there's nothing to pair, so the form is there whenever coins could run at all.
        CanPair = gate.Approved(MoneyMode.Play);
        var p = s.Money.Pairing;
        PairedAt = p?.PairedAt;
        State = p is null || s.Join is not { } join || !string.Equals(p.GridHost, join.Host, StringComparison.OrdinalIgnoreCase) ? PairState.NotPaired
            : join.MoneyFingerprint is { } published && !string.Equals(published, p.Fingerprint, StringComparison.OrdinalIgnoreCase) ? PairState.CertificateChanged
            : PairState.Paired;
        return true;
    }
}
