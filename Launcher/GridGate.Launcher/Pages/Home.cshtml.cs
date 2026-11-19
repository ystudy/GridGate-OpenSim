using System.Security.Claims;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Front;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages;

/// <summary>
/// The public front page (spec 2026-11-19-front-page-design): the grid's shop window for newcomers, with a photo, live numbers,
/// featured places, news and the way in. Anyone not signed in as staff gets it at "/" (SetupGate hands the bare root to this
/// page); it is open to every address (<c>AdminNetworkFilter</c>) and, with the other guest pages, does not exist on a Region
/// Server or a Connected Server (<c>GuestSideOnly</c>).
/// </summary>
public sealed class HomeModel(Func<LauncherSettings> settings, FrontPageView front) : PageModel
{
    public string GridName { get; private set; } = "";

    public FrontView View { get; private set; } = null!;

    /// <summary>Where the main button goes, the key of its words, and its icon.</summary>
    public (string Href, string Key, string Icon) Main => View.Main switch
    {
        FrontMainButton.YourAccount => ("/guest", "Home.YourAccount", "person"),
        FrontMainButton.CreateAccount => ("/signup", "Common.CreateAccount", "plus"),
        _ => ("/guest/sign-in", "Common.SignIn", "sign-in"),
    };

    public async Task OnGetAsync()
    {
        GridName = settings().GridName;
        // Forms in this page's frame (the top bar's sign-out) post to pages that see the guest cookie's user (or nobody), never
        // the staff one. Their antiforgery tokens are made for HttpContext.User, so the page takes that same user first.
        var guest = await HttpContext.AuthenticateAsync(AuthSchemes.Guest);
        HttpContext.User = guest.Succeeded ? guest.Principal! : new ClaimsPrincipal(new ClaimsIdentity());
        View = await front.BuildAsync(GridGate.Launcher.Resident.ResidentLanguageFilter.Current(HttpContext), guest.Succeeded,
            HttpContext.RequestAborted);
    }
}
