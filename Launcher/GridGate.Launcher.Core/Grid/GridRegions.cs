using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace GridGate.Launcher.Core.Grid;

/// <param name="X">The region's corner on the map, in map squares (Robust keeps it in metres, 256 to a square).</param>
/// <param name="IsLink">A hypergrid link to another grid's region (the Hyperlink flag, 512), not one of this grid's own.</param>
/// <param name="SizeX">The region's width in map squares (Robust keeps it in metres), at least 1.</param>
/// <param name="SizeY">The region's height in map squares, at least 1.</param>
public sealed record GridRegion(Guid Id, string Name, int X, int Y, bool IsLink = false, int SizeX = 1, int SizeY = 1);

/// <param name="Available">False when the database could not be asked; <see cref="Regions"/> is then empty.</param>
public sealed record GridRegionList(IReadOnlyList<GridRegion> Regions, bool Available)
{
    public static readonly GridRegionList Unavailable = new([], false);
}

/// <summary>The regions registered with the grid service, on every machine of the grid; the host can swap in a test double.</summary>
public interface IGridRegions
{
    /// <summary>Never throws for a database that is down or not set up yet: returns <see cref="GridRegionList.Unavailable"/>.</summary>
    Task<GridRegionList> ListAsync(CancellationToken ct);
}

/// <summary>
/// Reads Robust's regions table as robustuser: a region is listed while it is registered with Robust, whichever machine
/// runs it (the grid controller only knows this machine's regions). Shaped like <see cref="OnlineUsers"/>: settings read per
/// call, a short timeout, and any failure but the caller's cancellation gives "unavailable", never an error page.
/// </summary>
public sealed class GridRegions(Func<LauncherSettings> settings, ILogger<GridRegions>? log = null) : IGridRegions
{
    private int _warned;

    // Short: the splash page asks while viewers wait at the login screen.
    private const uint TimeoutSeconds = 3;

    private const string Query = "SELECT uuid, regionName, locX, locY, flags, sizeX, sizeY FROM regions ORDER BY regionName";

    // OpenSim's RegionFlags.Hyperlink: the row is a hypergrid link, not a region of this grid.
    private const int HyperlinkFlag = 512;

    /// <summary>A size Robust keeps in metres, in 256 m map squares; nothing, or less than one square, counts as one.</summary>
    public static int Squares(object? metres) =>
        metres is null or DBNull ? 1 : Math.Max(1, Convert.ToInt32(metres) / 256);

    public async Task<GridRegionList> ListAsync(CancellationToken ct)
    {
        try
        {
            var s = settings();
            await using var conn = await GridDb.OpenRobustAsync(s, TimeoutSeconds, ct);
            await using var cmd = new MySqlCommand(Query, conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<GridRegion>();
            while (await reader.ReadAsync(ct))
            {
                // char(36) may come back as a Guid or as text, depending on the connector's GUID handling.
                var id = InventoryStore.IdOf(reader.GetValue(0));
                list.Add(new GridRegion(id, reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)) / 256, reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3)) / 256,
                    !reader.IsDBNull(4) && (Convert.ToInt32(reader.GetValue(4)) & HyperlinkFlag) != 0,
                    Squares(reader.GetValue(5)), Squares(reader.GetValue(6))));
            }
            return new GridRegionList(list, true);
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return GridRegionList.Unavailable; // the database is down or still starting: expected, not logged
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var level = Interlocked.Exchange(ref _warned, 1) == 0 ? LogLevel.Warning : LogLevel.Debug;
            log?.Log(level, "The grid's regions could not be read ({Error}: {Message}).", e.GetType().Name, e.Message);
            return GridRegionList.Unavailable;
        }
    }
}
