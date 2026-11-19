using GridGate.Launcher.Core.Front;
using GridGate.Launcher.Core.Pictures;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Front;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// What anyone may fetch from the public front page (spec 2026-11-19-front-page-design, Constraints): a front picture only while
/// the page shows it, and a region's map tile only while that region is on the Automatic list. Nothing else, so no region or
/// picture is reachable that the page doesn't show. Both are 404 where there is no public side. Staff who may edit the page
/// (AdminPolicy) can also see any front picture at <see cref="StaffPictureUrl"/>, so the editor shows the own list's pictures
/// while another source is chosen.
/// </summary>
public static class FrontApi
{
    /// <summary>A picture's address never changes what it holds (a new picture gets a new name), so it may be kept for a year.</summary>
    private const string Immutable = "public, max-age=31536000, immutable";

    public static string PictureUrl(string name) => "/front/images/" + name;

    public static string MapUrl(Guid regionId) => $"/front/map/{regionId}.jpg";

    /// <summary>Any picture in the front store, for the editor only, as <c>/api/splash/pictures/{name}</c> is for the splash editor.</summary>
    public static string StaffPictureUrl(string name) => "/api/front/pictures/" + name;

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/front/pictures/{name}", (string name, [FromKeyedServices("front")] PictureStore pictures) =>
                pictures.Open(name) is { } file ? Results.File(file.Path, file.ContentType) : Results.NotFound())
            .RequireAuthorization(Security.AuthSchemes.AdminPolicy)
            .AddEndpointFilter(async (context, next) =>
                AdminRule.HasStaffPages(context.HttpContext.RequestServices.GetRequiredService<Func<LauncherSettings>>()())
                    ? await next(context) : Results.NotFound());

        app.MapGet("/front/images/{name}", (string name, HttpContext ctx, Func<LauncherSettings> settings, FrontPageStore store,
            [FromKeyedServices("front")] PictureStore pictures) =>
        {
            if (!AdminRule.HasStaffPages(settings()) || !FrontPictures.Shown(store.Load()).Contains(name) || pictures.Open(name) is not { } file)
                return Results.NotFound();
            ctx.Response.Headers.CacheControl = Immutable;
            return Results.File(file.Path, file.ContentType);
        }).AllowAnonymous();

        app.MapGet("/front/map/{id:guid}.jpg", async (Guid id, HttpContext ctx, Func<LauncherSettings> settings, FrontPageStore store,
            FrontPageSnapshot snapshot, FrontMapTiles tiles) =>
        {
            if (!AdminRule.HasStaffPages(settings()) || store.Load().PlacesSource != FrontPlacesSource.Automatic) return Results.NotFound();
            var live = await snapshot.CurrentAsync(ctx.RequestAborted);
            if (live.Busiest.FirstOrDefault(p => p.RegionId == id) is not { } place) return Results.NotFound();
            var (bytes, type) = await tiles.TileAsync(place, ctx.RequestAborted);
            if (bytes.Length == 0) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Bytes(bytes, type);
        }).AllowAnonymous();
    }
}
