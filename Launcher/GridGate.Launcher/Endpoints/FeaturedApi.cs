using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Featured;
using GridGate.Launcher.Front;
using GridGate.Launcher.Security;
using GridGate.Launcher.World;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// The Featured page's pictures (spec 2026-11-21-featured-places-design, "Constraints"): a region's map tile, only for a
/// region that has a place the asker may see on the page (Adult places only for a signed-in resident or staff member), through
/// the front page's shared tile cache. Anything else is 404, so no other region or tile is reachable; 404 where there is no
/// guest side.
/// </summary>
public static class FeaturedApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/featured/map/{id:guid}.jpg", async (Guid id, HttpContext ctx, Func<LauncherSettings> settings,
            FeaturedDirectory directory, FrontMapTiles tiles) =>
        {
            if (!AdminRule.HasStaffPages(settings())) return Results.NotFound();
            var signedIn = await WorldMapAccess.CheckAsync(ctx) == MapAccess.Allowed;
            var data = await directory.CurrentAsync(ctx.RequestAborted);
            var shown = FeaturedQuery.Visible(data, signedIn).Where(p => p.Region.Id == id).ToList();
            if (shown.Count == 0) return Results.NotFound();
            var (bytes, type) = await tiles.TileAsync(shown[0].Region, ctx.RequestAborted);
            if (bytes.Length == 0) return Results.NotFound();
            // A region a visitor's page shows too may be kept by shared caches; one only a signed-in page shows may not.
            var forVisitors = FeaturedQuery.Visible(data, signedIn: false).Any(p => p.Region.Id == id);
            ctx.Response.Headers.CacheControl = forVisitors ? "public, max-age=300" : "private, max-age=300";
            return Results.Bytes(bytes, type);
        }).AllowAnonymous();
    }
}
