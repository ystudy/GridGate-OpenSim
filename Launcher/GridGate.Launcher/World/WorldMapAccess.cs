using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Guests;
using GridGate.Launcher.Security;
using Microsoft.AspNetCore.Authentication;

namespace GridGate.Launcher.World;

/// <summary>Who may see the world map: someone signed in, a resident who still owes the Terms, or nobody yet.</summary>
public enum MapAccess { SignedOut, NeedsTerms, Allowed }

/// <summary>
/// The world map's door (spec 2026-11-20-public-map-design, "Who can see it"): a staff member signed in on the staff cookie, or
/// a resident signed in at the guest door who has accepted the current Terms. Each cookie is asked itself, as the guest layout
/// does, because the page and the data links are open routes. Outside the staff networks the staff cookie is ignored
/// (<see cref="StaffPrincipalValidator"/>), so staff there are signed out here too.
/// </summary>
public static class WorldMapAccess
{
    public static async Task<MapAccess> CheckAsync(HttpContext ctx)
    {
        var staff = (await ctx.AuthenticateAsync(AuthSchemes.Admin)).Principal;
        if (staff is not null && StaffSignIn.RoleOf(staff) != StaffRole.None) return MapAccess.Allowed;

        var resident = (await ctx.AuthenticateAsync(AuthSchemes.Guest)).Principal;
        if (resident is null || GuestSignIn.Read(resident) is not { } guest) return MapAccess.SignedOut;
        return TermsGate.MustAccept(ctx, guest.Principal) ? MapAccess.NeedsTerms : MapAccess.Allowed;
    }
}
