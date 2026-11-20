using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Plans;
using GridGate.Launcher.Core.Security;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.World;

namespace GridGate.Launcher.Endpoints;

/// <summary>
/// The world map's data (spec 2026-11-20-public-map-design, "The data links"): the grid's own regions and the map pictures, for
/// a signed-in resident or staff member only (<see cref="WorldMapAccess"/>), 401 for anyone else (no redirect: the page reloads
/// into its sign-in box). Both are 404 where there is no guest side.
/// </summary>
public static class WorldMapApi
{
    /// <summary>
    /// The start of a teleport link (<c>hop://host:port/</c>), from the login address viewers use; the page adds the region's
    /// name and the spot. Read per request, so a changed hostname or port shows at once.
    /// </summary>
    public static string TeleportStart(LauncherSettings s)
    {
        var login = new Uri(GridEndpoints.LoginUri(s, HostAddress.Effective(s.Hostname)));
        return $"hop://{login.Host}:{login.Port}/";
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/world/regions", async (HttpContext ctx, Func<LauncherSettings> settings, WorldMapSnapshot snapshot) =>
        {
            var s = settings();
            if (!AdminRule.HasStaffPages(s)) return Results.NotFound();
            if (await WorldMapAccess.CheckAsync(ctx) != MapAccess.Allowed) return Results.Unauthorized();
            ctx.Response.Headers.CacheControl = "no-store";
            var data = await snapshot.CurrentAsync(ctx.RequestAborted);
            return Results.Json(new
            {
                regions = data.Regions.Select(r => new { name = r.Name, x = r.X, y = r.Y, w = r.W, h = r.H, people = r.People }),
                available = data.Available,
                teleport = TeleportStart(s),
            });
        }).AllowAnonymous();

        app.MapGet("/world/tile/{z:int}/{x:int}/{y:int}", async (int z, int x, int y, HttpContext ctx, Func<LauncherSettings> settings,
            IGridController grid, IHttpClientFactory http) =>
        {
            if (!AdminRule.HasStaffPages(settings())) return Results.NotFound();
            if (await WorldMapAccess.CheckAsync(ctx) != MapAccess.Allowed) return Results.Unauthorized();
            return await MapTiles.TileAsync(z, x, y, grid, http, ctx, "private, max-age=300");
        }).AllowAnonymous();
    }
}
