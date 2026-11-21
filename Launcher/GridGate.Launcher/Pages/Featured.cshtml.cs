using System.Globalization;
using System.Security.Claims;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Endpoints;
using GridGate.Launcher.Featured;
using GridGate.Launcher.Front;
using GridGate.Launcher.Security;
using GridGate.Launcher.World;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages;

/// <summary>
/// The Featured places directory (spec 2026-11-21-featured-places-design): the parcels residents put in search, on this grid's
/// online regions, for anyone; Adult places only for a signed-in resident or staff member (<see cref="WorldMapAccess"/>). A
/// plain GET form searches, filters, sorts and pages, so it works without script. Open to every address
/// (<c>AdminNetworkFilter</c>); not there on a Region Server or a Connected Server (<c>GuestSideOnly</c>).
/// </summary>
public sealed class FeaturedModel(Func<LauncherSettings> settings, FeaturedDirectory directory, FrontMapTiles tiles) : PageModel
{
    public bool Available { get; private set; }

    public FeaturedView View { get; private set; } = null!;

    /// <summary>The stats row's tiles, each left out when its number is 0.</summary>
    public IReadOnlyList<FrontNumber> Numbers { get; private set; } = [];

    private string _teleport = "";

    public async Task OnGetAsync(string? q, string? category, string? rating, string? sort, string? page)
    {
        var signedIn = await WorldMapAccess.CheckAsync(HttpContext) == MapAccess.Allowed;

        // Forms in this page's frame (the top bar's sign-out) post to pages that see the guest cookie's user (or nobody), never
        // the staff one, so the page takes that same user first, as the map does.
        var guest = await HttpContext.AuthenticateAsync(AuthSchemes.Guest);
        HttpContext.User = guest.Succeeded ? guest.Principal! : new ClaimsPrincipal(new ClaimsIdentity());

        var data = await directory.CurrentAsync(HttpContext.RequestAborted);
        Available = data.Available;
        View = FeaturedQuery.View(data, FeaturedFilter.Parse(q, category, rating, sort, page), signedIn);
        _teleport = WorldMapApi.TeleportStart(settings());

        var culture = CultureInfo.CurrentUICulture;
        var stats = View.Stats;
        Numbers = new (string Key, long Value)[]
            {
                ("Featured.StatPlaces", stats.Places), ("Featured.StatVisits", stats.Visits),
                ("Featured.StatCategories", stats.Categories), ("Featured.StatRegions", stats.Regions),
            }
            .Where(n => n.Value > 0).Select(n => new FrontNumber(n.Key, n.Value.ToString("N0", culture))).ToList();
    }

    /// <summary>
    /// The place's <c>hop://</c> link, as the map makes them: to its landing point when it has one (whole metres), otherwise to
    /// the region, which then picks the spot.
    /// </summary>
    public string TeleportOf(FeaturedPlace p)
    {
        var link = _teleport + Uri.EscapeDataString(p.Region.Name);
        var parcel = p.Parcel;
        return parcel.HasLandingPoint
            ? link + string.Create(CultureInfo.InvariantCulture, $"/{(int)parcel.X}/{(int)parcel.Y}/{(int)parcel.Z}")
            : link;
    }

    /// <summary>Whether the grid answered recently that it has no map tile for the region: the card is drawn blank.</summary>
    public bool NoPicture(FeaturedPlace p) => tiles.KnownMissing(p.Region.Id);

    /// <summary>This page's address with the current choices and <paramref name="page"/>, for Previous and Next.</summary>
    public string PageLink(int page)
    {
        var f = View.Filter;
        var parts = new List<string>();
        if (f.Q is { } q) parts.Add("q=" + Uri.EscapeDataString(q));
        if (f.Category is { } c) parts.Add("category=" + c.ToString(CultureInfo.InvariantCulture));
        if (f.Rating is { } r) parts.Add("rating=" + r.ToString(CultureInfo.InvariantCulture));
        if (f.Sort != FeaturedFilter.SortVisits) parts.Add("sort=" + f.Sort);
        if (page > 1) parts.Add("page=" + page.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "/featured" : "/featured?" + string.Join("&", parts);
    }
}
