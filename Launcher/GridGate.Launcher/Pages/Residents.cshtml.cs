using System.Globalization;
using System.Security.Claims;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Front;
using GridGate.Launcher.Residents;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages;

/// <summary>
/// The Residents directory (spec 2026-11-21-resident-profiles-design, "The directory page"): residents who chose to be shown,
/// for anyone, so newcomers see real people live here. A plain GET form searches, sorts and pages, so it works without script.
/// Open to every address (<c>AdminNetworkFilter</c>); not there on a Region Server or a Connected Server (<c>GuestSideOnly</c>).
/// </summary>
public sealed class ResidentsModel(ResidentDirectory directory, FrontPageSnapshot live) : PageModel
{
    public bool Available { get; private set; }

    public ResidentView View { get; private set; } = null!;

    /// <summary>The stats row's tiles, each left out when its number is 0 or unknown.</summary>
    public IReadOnlyList<FrontNumber> Numbers { get; private set; } = [];

    public async Task OnGetAsync(string? q, string? sort, string? page)
    {
        // Forms in this page's frame (the top bar's sign-out) post to pages that see the guest cookie's user (or nobody), never
        // the staff one, so the page takes that same user first, as Featured does.
        var guest = await HttpContext.AuthenticateAsync(AuthSchemes.Guest);
        HttpContext.User = guest.Succeeded ? guest.Principal! : new ClaimsPrincipal(new ClaimsIdentity());

        var data = await directory.CurrentAsync(HttpContext.RequestAborted);
        var grid = await live.CurrentAsync(HttpContext.RequestAborted);
        Available = data.Available;
        View = ResidentQuery.View(data, ResidentFilter.Parse(q, sort, page));

        var culture = CultureInfo.CurrentUICulture;
        Numbers = new (string Key, long? Value)[]
            {
                ("Home.Registered", grid.Registered), ("Home.Active", grid.ActiveThisMonth),
                ("Residents.StatShown", View.Shown), ("Home.InWorld", grid.InWorld),
            }
            .Where(n => n.Value > 0).Select(n => new FrontNumber(n.Key, n.Value!.Value.ToString("N0", culture))).ToList();
    }

    /// <summary>
    /// The month and year the account was made, in the page language's own pattern ("November 2026", "noviembre de 2026"), or
    /// null when unknown.
    /// </summary>
    public static string? Since(PublicProfile p) => p.Created?.ToString("Y", CultureInfo.CurrentUICulture);

    /// <summary>This page's address with the current choices and <paramref name="page"/>, for Previous and Next.</summary>
    public string PageLink(int page)
    {
        var f = View.Filter;
        var parts = new List<string>();
        if (f.Q is { } q) parts.Add("q=" + Uri.EscapeDataString(q));
        if (f.Sort != ResidentFilter.SortNewest) parts.Add("sort=" + f.Sort);
        if (page > 1) parts.Add("page=" + page.ToString(CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "/residents" : "/residents?" + string.Join("&", parts);
    }
}
