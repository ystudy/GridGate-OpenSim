using System.Security.Claims;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GridGate.Launcher.Pages.Guest;

/// <summary>
/// Signing out of the guest pages is a POST (with the antiforgery token), so a link or an image cannot do it. It removes the
/// guest cookie only; a staff cookie in the same browser stays.
/// </summary>
/// <remarks>
/// The form is in the top bar of every public page (_GuestLayout), and its token is the one that page made, for the page's own
/// <c>HttpContext.User</c>: the resident on /guest/* and /home, but nobody on pages under the default (staff) scheme such as
/// /support, /signup and the legal pages. Here <c>User</c> is the resident (the Guest policy), so the automatic check would
/// refuse those pages' tokens. Instead the token is checked against the resident, then nobody, as <c>LanguageModel</c> does; the
/// cookie and form token pair is still required.
/// </remarks>
[IgnoreAntiforgeryToken]
public sealed class SignOutModel(IAntiforgery antiforgery) : PageModel
{
    public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await TokenIsValidAsync())
            return BadRequest();
        await HttpContext.SignOutAsync(AuthSchemes.Guest);
        return Redirect("/");
    }

    private async Task<bool> TokenIsValidAsync()
    {
        var own = HttpContext.User;
        try
        {
            foreach (var who in new[] { own, new ClaimsPrincipal(new ClaimsIdentity()) })
            {
                HttpContext.User = who;
                if (await antiforgery.IsRequestValidAsync(HttpContext)) return true;
            }
            return false;
        }
        finally
        {
            HttpContext.User = own;
        }
    }
}
