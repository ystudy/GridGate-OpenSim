using System.Globalization;
using System.Security.Claims;
using GridGate.Launcher.Core.Guests;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Front;
using GridGate.Launcher.Security;
using GridGate.Launcher.World;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages;

/// <summary>
/// The world map (spec 2026-11-20-public-map-design): the grid's own regions to look around and teleport into, for a signed-in
/// resident or staff member (<see cref="WorldMapAccess"/>); anyone else gets an invitation to sign in, with the live numbers and
/// no region at all. Open to every address (<c>AdminNetworkFilter</c>); not there on a Region Server or a Connected Server
/// (<c>GuestSideOnly</c>). The map itself is drawn by map-core.js and world-map.js from <c>/world/regions</c> and <c>/world/tile</c>.
/// </summary>
public sealed class MapModel(Func<LauncherSettings> settings, FrontPageSnapshot snapshot) : PageModel
{
    public bool ShowMap { get; private set; }

    /// <summary>For the invitation: regions online and people in-world, each left out when it can't be counted.</summary>
    public IReadOnlyList<FrontNumber> Numbers { get; private set; } = [];

    public bool SignUpOpen { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var access = await WorldMapAccess.CheckAsync(HttpContext);
        if (access == MapAccess.NeedsTerms) return Redirect("/guest/terms?ReturnUrl=" + Uri.EscapeDataString("/map"));

        // Forms in this page's frame (the top bar's sign-out) post to pages that see the guest cookie's user (or nobody), never
        // the staff one, so the page takes that same user first, as the front page does.
        var guest = await HttpContext.AuthenticateAsync(AuthSchemes.Guest);
        HttpContext.User = guest.Succeeded ? guest.Principal! : new ClaimsPrincipal(new ClaimsIdentity());

        ShowMap = access == MapAccess.Allowed;
        if (!ShowMap)
        {
            SignUpOpen = SignUpRules.IsOpen(settings());
            var live = await snapshot.CurrentAsync(HttpContext.RequestAborted);
            var culture = CultureInfo.CurrentUICulture;
            var list = new List<FrontNumber>();
            if (live.RegionsOnline is { } regions) list.Add(new FrontNumber("Home.RegionsOnline", regions.ToString("N0", culture)));
            if (live.InWorld is { } people) list.Add(new FrontNumber("Home.InWorld", people.ToString("N0", culture)));
            Numbers = list;
        }
        return Page();
    }
}
