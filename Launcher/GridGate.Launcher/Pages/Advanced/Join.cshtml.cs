using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Security;
using GridGate.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Advanced;

/// <summary>
/// A finished Region Server's Grid Asset Server, which only the owner re-points (every confirm is in the audit log): the same read, show and confirm as the setup join step
/// (<see cref="JoinForm"/>), but the confirm rewrites the INIs and asks for a restart. Any other plan, or a setup not
/// finished, goes back to Advanced settings. Nothing is saved until the new grid has answered: the estate owner must be on it and
/// accept the typed password, and none of our regions may overlap a region on its map. The owner also enters a current two-factor code
/// of theirs in the same form (kept on this computer, so the grid's staff can't reset it); without two-factor on, the page says where to
/// turn it on and the confirm is refused. Then everything is saved in one write.
/// </summary>
public sealed class JoinModel(IGridController grid, Func<LauncherSettings> settings, IGridJoinClient join, RobustClientFor robustFor, AuditLog audit,
    TwoFactorStore twoFactor, LoginThrottle throttle, ILogger<JoinModel> log) : PageModel
{
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    [BindProperty] public string? Address { get; set; }

    /// <summary>The owner's password on the new grid, checked with that grid before the re-point is kept. Never shown back, logged or saved.</summary>
    [BindProperty] public string? Password { get; set; }

    /// <summary>True when the settings name an estate owner, whose password on the new grid the confirm view asks for.</summary>
    public bool AsksPassword => settings().Setup.Owner is not null;

    /// <summary>The owner's current two-factor code, checked just before the re-point is kept. Never shown back, logged or saved.</summary>
    [BindProperty] public string? TwoFactorCode { get; set; }

    /// <summary>The owner has two-factor on here: the confirm view asks for a code, otherwise it says where to turn two-factor on.</summary>
    public bool TwoFactorOn => OwnerCodeStep.IsSetUp(twoFactor, User);

    /// <summary>The join information the first post read, shown with "Join this grid"; null shows the address form.</summary>
    public JoinedGrid? Found { get; private set; }

    /// <summary>Why the address can't be used or the join information couldn't be read, for the owner.</summary>
    public string? Error { get; private set; }

    /// <summary>The Grid Asset Server this computer uses now, as the owner would type it.</summary>
    public string Current { get; private set; } = "";

    /// <summary>Set after a save: the "restart your world" notice (no extra notes).</summary>
    public IReadOnlyList<string>? SavedNotes { get; private set; }

    public IActionResult OnGet()
    {
        if (Closed() is { } closed) return closed;
        Address = Current;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Closed() is { } closed) return closed;
        Password = null;
        TwoFactorCode = null;
        (Found, Error) = await JoinForm.ReadAsync(join, Address, HttpContext.RequestAborted);
        if (Found is not null) Address = JoinForm.AddressOf(Found);
        return Page();
    }

    /// <summary>"Change the address": the form again, with the address filled in.</summary>
    public IActionResult OnPostChange()
    {
        Password = null;
        TwoFactorCode = null;
        return Closed() ?? Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        if (Closed() is { } closed) return closed;
        var ct = HttpContext.RequestAborted;
        var password = Password;
        var typedCode = TwoFactorCode;
        Password = null;
        TwoFactorCode = null;
        var (read, error) = await JoinForm.ReadAsync(join, Address, ct);
        if (read is null)
        {
            audit.Write(User, Ip, "grid-join", LogText.Clean(Address, 200), "refused");
            Error = error;
            return Page();
        }

        // The owner here is a grid account, which the Grid Asset Server's admins can reset: so a current two-factor code of this
        // computer's own is needed too. Without two-factor on, nothing more is asked.
        if (!OwnerCodeStep.IsSetUp(twoFactor, User))
            return Refuse(read, OwnerCodeStep.Words(OwnerCodeResult.NotSetUp), OwnerCodeStep.AuditResult(OwnerCodeResult.NotSetUp));

        var before = settings();
        if (before.Setup.Owner is not null && string.IsNullOrWhiteSpace(password))
            return Refuse(read, $"Type your password on {read.GridName}.");

        // Everything is checked with the new grid before anything is saved: the estate owner's account id belongs to
        // the old grid, so the owner is looked up there by name and must accept the typed password, and none of our
        // regions may overlap a region on its map. Only then are the join, grid name and owner saved, in one write.
        var target = JoinForm.Apply(before, read);
        var newGrid = robustFor(target);
        RobustAccount? account = null;
        try
        {
            if (before.Setup.Owner is { } owner)
            {
                var (first, last) = (owner.FirstName.Trim(), owner.LastName.Trim());
                account = await newGrid.GetAccountAsync(first, last, ct);
                if (account is null)
                    return Refuse(read, $"There's no avatar called {first} {last} on {read.GridName}. Create your avatar on that grid first, then try again.");
                if (!await newGrid.AuthenticateAsync(account.PrincipalId, password!, ct))
                    return Refuse(read, $"That isn't the password of {account.FirstName} {account.LastName} on {read.GridName}. Nothing was changed.");
            }
            var map = await newGrid.GetMapRegionsAsync(ct);
            foreach (var region in target.Regions.Where(r => r.DeletedAt is null))
            {
                var check = RegionPlacement.Check(target, region.X, region.Y, region.Size, ignore: region.Uuid, elsewhere: map);
                if (!check.Ok)
                    return Refuse(read,
                        $"Your region {region.Name} would overlap {check.OverlapsWith} on {read.GridName}. Move it first (Regions page), then try again.");
            }
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Refuse(read, GridAssetUnreachableException.MessageFor(read.Host, read.PublicPort));
        }

        // The code is checked last, so a wrong password or an overlap doesn't use one up; nothing is saved before it passes.
        var code = OwnerCodeStep.Check(twoFactor, throttle, log, User, HttpContext.Connection.RemoteIpAddress, typedCode);
        if (code != OwnerCodeResult.Ok)
            return Refuse(read, OwnerCodeStep.Words(code), OwnerCodeStep.AuditResult(code));

        var droppedGloebit = target.Money.Gloebit is null && before.Money.Gloebit is { Key.Length: > 0 };
        grid.UpdateSettings(s => account is null
            ? JoinForm.Apply(s, read)
            : JoinForm.Apply(s, read) with { Setup = s.Setup with { OwnerCreated = true, Owner = new OwnerInfo(account.FirstName, account.LastName, account.PrincipalId) } },
            writeConfig: true);
        audit.Write(User, Ip, "grid-join", JoinForm.AddressOf(read));
        if (droppedGloebit) GloebitAppNotice.Removed(audit, User, Ip, before.Money.Gloebit!.Key);
        Current = JoinForm.AddressOf(read);
        SavedNotes = PortWarning(settings(), read) is { } warning ? [warning] : [];
        return Page();
    }

    /// <summary>
    /// A warning when this computer's first region port lies in the region ports the Grid Asset Server uses (behind one
    /// router the two would clash); null otherwise.
    /// </summary>
    private static string? PortWarning(LauncherSettings s, JoinedGrid read) =>
        read.RegionPortsFrom is { } from && read.RegionPortsTo is { } to && s.RegionPort >= from && s.RegionPort <= to
            ? $"This computer's region ports overlap the Grid Asset Server's ({from}-{to}). If both computers are behind one router, "
              + "change this computer's first region port in Settings → Network."
            : null;

    /// <summary>Nothing was saved: writes the "refused" audit line and shows the confirm view with <paramref name="error"/>.</summary>
    private PageResult Refuse(JoinedGrid read, string error, string result = "refused")
    {
        audit.Write(User, Ip, "grid-join", JoinForm.AddressOf(read), result);
        Error = error;
        Found = read;
        Address = JoinForm.AddressOf(read);
        return Page();
    }

    private IActionResult? Closed()
    {
        var s = settings();
        if (s.Plan != OwnerPlan.RegionServer || !s.Setup.Finished) return Redirect("/advanced");
        Current = s.Join is { } joined ? JoinForm.AddressOf(joined) : "";
        return null;
    }
}
