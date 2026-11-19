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
}
