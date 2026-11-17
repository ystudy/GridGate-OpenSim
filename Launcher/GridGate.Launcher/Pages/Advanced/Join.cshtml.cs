using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.OpenSim;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Security;
using GridGate.Launcher.Setup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Advanced;

/// <summary>
/// A finished Region Server's Grid Asset Server, which only the owner re-points (every confirm is in the audit log): the same read, show and confirm as the setup join step
/// (<see cref="JoinForm"/>), but the confirm rewrites the INIs and asks for a restart. Any other plan, or a setup not
/// finished, goes back to Advanced settings. Nothing is saved while the address or the read fails; the new join is put
/// back when the estate owner isn't on the new grid or one of our regions would overlap a region on its map.
/// </summary>
public sealed class JoinModel(IGridController grid, Func<LauncherSettings> settings, IGridJoinClient join, IRobustClient robust, AuditLog audit) : PageModel
{
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    [BindProperty] public string? Address { get; set; }

    /// <summary>The owner's password on the new grid, checked with that grid before the re-point is kept. Never shown back, logged or saved.</summary>
    [BindProperty] public string? Password { get; set; }

    /// <summary>True when the settings name an estate owner, whose password on the new grid the confirm view asks for.</summary>
    public bool AsksPassword => settings().Setup.Owner is not null;

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
        (Found, Error) = await JoinForm.ReadAsync(join, Address, HttpContext.RequestAborted);
        if (Found is not null) Address = JoinForm.AddressOf(Found);
        return Page();
    }

    /// <summary>"Change the address": the form again, with the address filled in.</summary>
    public IActionResult OnPostChange() => Closed() ?? Page();

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        if (Closed() is { } closed) return closed;
        var ct = HttpContext.RequestAborted;
        var password = Password;
        Password = null;
        var (read, error) = await JoinForm.ReadAsync(join, Address, ct);
        if (read is null)
        {
            audit.Write(User, Ip, "grid-join", LogText.Clean(Address, 200), "refused");
            Error = error;
            return Page();
        }

        // The estate owner's account id belongs to the old grid: save the new join, then look the owner up on the
        // new grid (which saves the new id). If that fails, or one of our regions would overlap a region on the new
        // grid's map, the old join, grid name and owner are put back.
        var before = settings();
        if (before.Setup.Owner is not null && string.IsNullOrWhiteSpace(password))
        {
            audit.Write(User, Ip, "grid-join", JoinForm.AddressOf(read), "refused");
            Error = $"Type your password on {read.GridName}.";
            Found = read;
            Address = JoinForm.AddressOf(read);
            return Page();
        }
        grid.UpdateSettings(s => JoinForm.Apply(s, read), writeConfig: true);
        if (before.Setup.Owner is { } owner)
        {
            try
            {
                var account = await grid.SetEstateOwnerAsync(owner.FirstName, owner.LastName, ct);
                // Only the owner's own password on the new grid keeps the move: the robust client asks the grid the settings now name.
                if (!await robust.AuthenticateAsync(account.PrincipalId, password!, ct))
                    return Refuse(before, read, $"That isn't the password of {account.FirstName} {account.LastName} on {read.GridName}. Nothing was changed.");
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                return Refuse(before, read, GridAssetUnreachableException.MessageFor(read.Host, read.PublicPort));
            }
            catch (Exception e) when (e is ArgumentException or GridUnreachableException)
            {
                return Refuse(before, read,
                    e is ArgumentException ? e.Message + " Create your avatar on that grid first, then try again." : e.Message);
            }
        }

        // The robust client asks the grid the settings now name: the new one.
        IReadOnlyList<MapRegion> map;
        try
        {
            map = await robust.GetMapRegionsAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Refuse(before, read, GridAssetUnreachableException.MessageFor(read.Host, read.PublicPort));
        }
        var saved = settings();
        foreach (var region in saved.Regions.Where(r => r.DeletedAt is null))
        {
            var check = RegionPlacement.Check(saved, region.X, region.Y, region.Size, ignore: region.Uuid, elsewhere: map);
            if (!check.Ok)
                return Refuse(before, read,
                    $"Your region {region.Name} would overlap {check.OverlapsWith} on {read.GridName}. Move it first (Regions page), then try again.");
        }

        audit.Write(User, Ip, "grid-join", JoinForm.AddressOf(read));
        Current = JoinForm.AddressOf(read);
        SavedNotes = PortWarning(saved, read) is { } warning ? [warning] : [];
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

    /// <summary>Puts back the join, grid name, estate owner, money pairing and Gloebit app saved <paramref name="before"/>, and shows the confirm view with <paramref name="error"/>.</summary>
    private PageResult Refuse(LauncherSettings before, JoinedGrid read, string error)
    {
        audit.Write(User, Ip, "grid-join", JoinForm.AddressOf(read), "refused");
        grid.UpdateSettings(s => s with
        {
            Join = before.Join,
            GridName = before.GridName,
            Setup = s.Setup with { OwnerCreated = before.Setup.OwnerCreated, Owner = before.Setup.Owner },
            // Joining dropped a money pairing with the old Grid Asset Server; a refused re-point keeps it.
            // The Gloebit app belongs to the old grid and is put back with it.
            Money = s.Money with { Pairing = before.Money.Pairing, Gloebit = before.Money.Gloebit },
        }, writeConfig: true);
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
