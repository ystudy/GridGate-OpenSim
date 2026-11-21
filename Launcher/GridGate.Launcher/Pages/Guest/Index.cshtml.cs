using System.Globalization;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Guests;
using GridGate.Launcher.ModelImport;
using GridGate.Launcher.Residents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>
/// A signed-in resident's home, the Avatar page's Avatar tab (spec 2026-11-21-avatar-page-design; <c>AuthSchemes.GuestPolicy</c>,
/// by the /Guest folder convention): their avatar's picture, name, member since and where they are, what it wears, how to get
/// in-world, and their other pages. Everything on it is their own.
/// </summary>
public sealed class IndexModel(Func<LauncherSettings> settings, RegionRights rights, ResidentTwoFactor residents, UploadRights uploads,
    GridGate.Launcher.Guests.IarRights iarRights, IPublicProfiles profiles, PublicProfileChoices publicProfiles, AvatarLooks looks,
    IOnlineUsers online, IGridRegions regions) : PageModel
{
    /// <summary>Whether the page links to the resident's two-factor page (<see cref="ResidentTwoFactor.PageShown"/>).</summary>
    public bool TwoFactorLink { get; private set; }

    public string DisplayName { get; private set; } = "";
    public string GridName { get; private set; } = "";

    /// <summary>The login address to paste into the viewer's grid manager.</summary>
    public string LoginUri { get; private set; } = "";

    /// <summary>Whether the guest has rights over any region now (the page links to their regions).</summary>
    public bool OwnsRegions { get; private set; }

    /// <summary>Whether the guest may upload models here (the same rule as the upload pages, <see cref="UploadPagesOnlyAttribute"/>).</summary>
    public bool UploadLink { get; private set; }
    public bool InventoryLink { get; private set; }

    /// <summary>Whether they have a profile picture (the page then shows <c>/guest/avatar.jpg</c>).</summary>
    public bool HasPicture { get; private set; }

    /// <summary>"November 2026", or null when unknown.</summary>
    public string? Since { get; private set; }

    /// <summary>In-world now; <see cref="Region"/> names where, when it is known.</summary>
    public bool InWorld { get; private set; }
    public string? Region { get; private set; }

    /// <summary>Their own Residents page address, when they are on it.</summary>
    public string? PublicPage { get; private set; }

    public AvatarLookResult Look { get; private set; } = new(AvatarLookState.Unavailable);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (GuestSignIn.Read(User) is not { } guest) return Challenge();
        var me = guest.Principal;
        var s = settings();
        OwnsRegions = rights.OwnedBy(me).Count > 0;
        TwoFactorLink = ResidentTwoFactor.PageShown(s, residents.Store.StateOf(me));
        UploadLink = UploadPagesOnlyAttribute.Shown(s, uploads, User);
        InventoryLink = GridGate.Launcher.Iar.IarPagesOnlyAttribute.Shown(s, iarRights, User);
        DisplayName = User.Identity?.Name ?? "";
        GridName = s.GridName;
        LoginUri = GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname));

        // The four reads go side by side; each gives "nothing" rather than an error when its database is down.
        var profile = profiles.ReadAsync([me], ct);
        var look = looks.ReadAsync(me, ct);
        var who = online.ListAsync(ct);
        var where = regions.ListAsync(ct);
        if ((await profile).Profiles.FirstOrDefault(p => p.Id == me) is { } p)
        {
            HasPicture = p.ImageId != Guid.Empty;
            Since = p.Created?.ToString("Y", CultureInfo.CurrentUICulture);
            if (publicProfiles.StateOf(me) == PublicProfileState.On) PublicPage = ResidentQuery.LinkOf(p);
        }
        Look = await look;
        var regionList = await where;
        if ((await who).Avatars.FirstOrDefault(a => a.PersonId == me) is { } here)
        {
            InWorld = true;
            Region = regionList.Regions.FirstOrDefault(r => r.Id == here.RegionId)?.Name;
        }
        return Page();
    }
}
