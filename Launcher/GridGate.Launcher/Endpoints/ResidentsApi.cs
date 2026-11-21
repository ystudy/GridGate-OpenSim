using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Featured;
using GridGate.Launcher.Residents;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// The Residents pages' pictures (spec 2026-11-21-resident-profiles-design, "Pictures"): a shown resident's profile picture and
/// their picks' snapshots, as JPEGs through <see cref="ParcelPictures"/>. Anything else is 404, so no other account's picture
/// and no other texture is reachable; 404 where there is no guest side.
/// </summary>
public static class ResidentsApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/residents/picture/{id:guid}.jpg", async (Guid id, HttpContext ctx, Func<LauncherSettings> settings,
            ResidentDirectory directory, ParcelPictures pictures) =>
        {
            if (!AdminRule.HasStaffPages(settings())) return Results.NotFound();
            var data = await directory.CurrentAsync(ctx.RequestAborted);
            if (data.Profiles.FirstOrDefault(p => p.Id == id) is not { } profile) return Results.NotFound();
            return await JpegAsync(ctx, pictures, profile.ImageId);
        }).AllowAnonymous();

        app.MapGet("/residents/pick/{id:guid}.jpg", async (Guid id, HttpContext ctx, Func<LauncherSettings> settings,
            ResidentDirectory directory, ParcelPictures pictures) =>
        {
            if (!AdminRule.HasStaffPages(settings())) return Results.NotFound();
            var data = await directory.CurrentAsync(ctx.RequestAborted);
            if (ResidentQuery.FindPick(data, id) is not { } found) return Results.NotFound();
            return await JpegAsync(ctx, pictures, found.Pick.SnapshotId);
        }).AllowAnonymous();
    }

    private static async Task<IResult> JpegAsync(HttpContext ctx, ParcelPictures pictures, Guid texture)
    {
        var bytes = await pictures.JpegAsync(texture, ctx.RequestAborted);
        if (bytes.Length == 0) return Results.NotFound();
        // Short, so a resident who turns the page off is gone from shared caches within minutes.
        ctx.Response.Headers.CacheControl = "public, max-age=300";
        return Results.Bytes(bytes, "image/jpeg");
    }
}
