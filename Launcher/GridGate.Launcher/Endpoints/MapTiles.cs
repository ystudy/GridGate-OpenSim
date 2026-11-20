using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Grid;

namespace GridGate.Launcher.Endpoints;

/// <summary>A region's map tile from Robust's map service, as the map page's tile proxy fetches it.</summary>
public static class MapTiles
{
    /// <summary>
    /// The tile's bytes and type; empty bytes when the grid has no tile for the region yet or Robust didn't answer. A missing
    /// tile can come back as a 200 with text/plain and an empty body, so only a JPEG or PNG counts.
    /// </summary>
    public static async Task<(byte[] Bytes, string ContentType)> FetchAsync(GridRegion region, IGridController grid, IHttpClientFactory http,
        CancellationToken ct)
    {
        try
        {
            using var client = http.CreateClient(RegionsApi.RobustMapClient);
            var port = grid.CurrentSettings().RobustPublicPort;
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/map-1-{region.X}-{region.Y}-objects.jpg", ct);
            var type = response.Content.Headers.ContentType?.MediaType;
            return response.IsSuccessStatusCode && type is "image/jpeg" or "image/png"
                ? (await response.Content.ReadAsByteArrayAsync(ct), type!)
                : ([], "");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ([], "");
        }
    }

    /// <summary>
    /// A map tile for the map pages, from Robust through the launcher (never Robust's port in the browser): 400 for a zoom or
    /// corner that isn't one (a zoom z tile spans 2^(z-1) squares, so its corner is a multiple of that), 404 when Robust has no
    /// picture or doesn't answer. Only validated integers reach Robust's URL; the port is read per request.
    /// </summary>
    public static async Task<IResult> TileAsync(int z, int x, int y, IGridController grid, IHttpClientFactory http, HttpContext ctx,
        string cacheControl)
    {
        if (z is < 1 or > 8 || x is < 0 or > RegionPlacement.MaxCoordinate || y is < 0 or > RegionPlacement.MaxCoordinate)
            return Results.BadRequest();
        var span = 1 << (z - 1);
        if (x % span != 0 || y % span != 0)
            return Results.BadRequest();

        var port = grid.CurrentSettings().RobustPublicPort;
        try
        {
            using var client = http.CreateClient(RegionsApi.RobustMapClient);
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/map-{z}-{x}-{y}-objects.jpg", ctx.RequestAborted);
            // A missing tile can come back as a 200 with text/plain and an empty or short body.
            var type = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || type is not ("image/jpeg" or "image/png"))
                return Results.NotFound();
            var bytes = await response.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
            if (bytes.Length == 0)
                return Results.NotFound();
            ctx.Response.Headers.CacheControl = cacheControl;
            return Results.File(bytes, type);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.NotFound();
        }
    }
}
