using System.Globalization;
using System.Security.Claims;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Endpoints;
using GridGate.Launcher.Residents;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages;

/// <summary>
/// One resident's public profile (spec 2026-11-21-resident-profiles-design, "The profile page"): what they filled in through their
/// viewer, for anyone, only while they choose to be shown. Anyone else's name is 404, the same as a name nobody has.
/// </summary>
public sealed class ResidentProfileModel(ResidentDirectory directory, Func<LauncherSettings> settings) : PageModel
{
    public PublicProfile Profile { get; private set; } = null!;

    public Uri? WebLink { get; private set; }

    private ResidentData _data = null!;
    private string _teleport = "";

    public async Task<IActionResult> OnGetAsync(string? first, string? last)
    {
        var guest = await HttpContext.AuthenticateAsync(AuthSchemes.Guest);
        HttpContext.User = guest.Succeeded ? guest.Principal! : new ClaimsPrincipal(new ClaimsIdentity());

        _data = await directory.CurrentAsync(HttpContext.RequestAborted);
        if (ResidentQuery.Find(_data, first, last) is not { } profile) return NotFound();
        Profile = profile;
        WebLink = ResidentQuery.SafeWebLink(profile.WebUrl);
        _teleport = WorldMapApi.TeleportStart(settings());
        return Page();
    }

    public string? Since => ResidentsModel.Since(Profile);

    /// <summary>
    /// The pick's <c>hop://</c> link to its spot, when its region is one of this grid's own regions online now and the spot is
    /// inside it; null otherwise (an old pick, a region that is down or on another grid).
    /// </summary>
    public string? TeleportOf(PublicPick pick)
    {
        if (!_data.Regions.TryGetValue(pick.RegionName, out var region)) return null;
        var (x, y) = (pick.X - region.X * 256f, pick.Y - region.Y * 256f);
        if (x < 0 || y < 0 || x >= region.SizeX * 256f || y >= region.SizeY * 256f) return _teleport + Uri.EscapeDataString(region.Name);
        return _teleport + Uri.EscapeDataString(region.Name)
            + string.Create(CultureInfo.InvariantCulture, $"/{(int)x}/{(int)y}/{(int)Math.Max(0, pick.Z)}");
    }
}
