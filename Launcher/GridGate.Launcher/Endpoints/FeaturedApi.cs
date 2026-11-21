using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Featured;
using GridGate.Launcher.Front;
using GridGate.Launcher.Security;
using GridGate.Launcher.World;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// The Featured page's pictures (spec 2026-11-21-featured-places-design, "Pictures"): for a place the asker may see on the
/// page (Adult places only for a signed-in resident or staff member), its own snapshot (<see cref="ParcelPictures"/>), else its
/// region's map tile through the front page's shared tile cache. Anything else is 404, so no other parcel, region or texture
/// is reachable; 404 where there is no guest side.
/// </summary>
public static class FeaturedApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/featured/picture/{id:guid}.jpg", async (Guid id, HttpContext ctx, Func<LauncherSettings> settings,
            FeaturedDirectory directory, ParcelPictures snapshots, FrontMapTiles tiles) =>
        {
            if (!AdminRule.HasStaffPages(settings())) return Results.NotFound();
            var signedIn = await WorldMapAccess.CheckAsync(ctx) == MapAccess.Allowed;
            var data = await directory.CurrentAsync(ctx.RequestAborted);
            if (FeaturedQuery.Visible(data, signedIn).FirstOrDefault(p => p.Parcel.Id == id) is not { } place) return Results.NotFound();

            var (bytes, type) = (await snapshots.JpegAsync(place.Parcel.SnapshotId, ctx.RequestAborted), "image/jpeg");
            if (bytes.Length == 0) (bytes, type) = await tiles.TileAsync(place.Region, ctx.RequestAborted);
            if (bytes.Length == 0) return Results.NotFound();
            // A place a visitor's page shows too may be kept by shared caches; one only a signed-in page shows may not.
            ctx.Response.Headers.CacheControl = place.Parcel.Rating < 2 ? "public, max-age=300" : "private, max-age=300";
            return Results.Bytes(bytes, type);
        }).AllowAnonymous();
    }
}
