using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace GridGate.Launcher.Core.Grid;

/// <summary>A parcel its owner put in search (About Land, "Show in search").</summary>
/// <param name="Category">OpenSim's parcel category, 0 (none) to 13 (other); anything else reads as 13.</param>
/// <param name="Rating">0 General, 1 Moderate, 2 Adult (<see cref="ListedParcels.RatingOf"/>).</param>
/// <param name="Visits">The parcel's traffic: recent visits, as GridGate's region module keeps it in <c>land.Dwell</c>.</param>
/// <param name="HasLandingPoint">The parcel sends arrivals to a set landing point (<see cref="X"/>, <see cref="Y"/>, <see cref="Z"/>).</param>
public sealed record ListedParcel(Guid Id, Guid RegionId, string Name, string Description, int Category, int Rating, int Visits,
    bool HasLandingPoint, float X, float Y, float Z);

/// <param name="Available">False when the database could not be asked; <see cref="Parcels"/> is then empty.</param>
public sealed record ListedParcelList(IReadOnlyList<ListedParcel> Parcels, bool Available)
{
    public static readonly ListedParcelList Unavailable = new([], false);
}

/// <summary>The parcels residents put in search, on every region the grid stores; the host can swap in a test double.</summary>
public interface IListedParcels
{
    /// <summary>Never throws for a database that is down or not set up yet: returns <see cref="ListedParcelList.Unavailable"/>.</summary>
    Task<ListedParcelList> ListAsync(CancellationToken ct);
}

/// <summary>
/// Reads OpenSim's <c>land</c> table (and each region's rating from <c>regionsettings</c>) as opensimuser (spec
/// 2026-11-21-featured-places-design). Shaped like <see cref="GridRegions"/>: settings read per call, a short timeout, and any
/// failure but the caller's cancellation gives "unavailable". A grid whose regions haven't made the tables yet has none.
/// </summary>
public sealed class ListedParcels(Func<LauncherSettings> settings, ILogger<ListedParcels>? log = null) : IListedParcels
{
    /// <summary>OpenMetaverse's <c>ParcelFlags.ShowDirectory</c>: the owner ticked "Show in search".</summary>
    public const uint ShowInSearch = 0x1000;

    /// <summary>OpenMetaverse's <c>ParcelFlags.MaturePublish</c>: the owner ticked "Mature content".</summary>
    public const uint MatureContent = 0x40000;

    /// <summary>OpenMetaverse's <c>ParcelCategory.Adult</c>.</summary>
    public const int AdultCategory = 2;

    public const int LastCategory = 13;

    private const uint TimeoutSeconds = 3;

    // OpenSim's LandingType 1: arrivals go to the parcel's landing point (UserLocation).
    private const int LandingPoint = 1;

    private const string Query =
        "SELECT l.UUID, l.RegionUUID, l.Name, l.Description, l.Category, l.LandFlags, l.Dwell, l.LandingType, " +
        "l.UserLocationX, l.UserLocationY, l.UserLocationZ, rs.maturity " +
        "FROM land l LEFT JOIN regionsettings rs ON rs.regionUUID = l.RegionUUID WHERE (l.LandFlags & 4096) <> 0";

    private int _warned;

    /// <summary>
    /// A place's rating: its region's maturity (0 General, 1 Moderate, 2 Adult; unknown reads as Adult, so a region whose
    /// settings can't be read is never shown to strangers), raised to Adult
    /// in the Adult category and to at least Moderate when the owner marked it "Mature content".
    /// </summary>
    public static int RatingOf(int? regionMaturity, int category, uint flags)
    {
        var rating = Math.Clamp(regionMaturity ?? 2, 0, 2);
        if (category == AdultCategory) rating = 2;
        if ((flags & MatureContent) != 0) rating = Math.Max(rating, 1);
        return rating;
    }

    public async Task<ListedParcelList> ListAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await GridDb.OpenOpensimAsync(settings(), TimeoutSeconds, ct);
            await using var cmd = new MySqlCommand(Query, conn) { CommandTimeout = (int)TimeoutSeconds };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<ListedParcel>();
            while (await r.ReadAsync(ct))
            {
                var category = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4));
                if (category is < 0 or > LastCategory) category = LastCategory;
                var flags = r.IsDBNull(5) ? 0u : Convert.ToUInt32(r.GetValue(5));
                var (x, y, z) = (Float(r, 8), Float(r, 9), Float(r, 10));
                list.Add(new ListedParcel(
                    InventoryStore.IdOf(r.GetValue(0)), InventoryStore.IdOf(r.GetValue(1)),
                    Text(r, 2), Text(r, 3), category,
                    RatingOf(r.IsDBNull(11) ? null : Convert.ToInt32(r.GetValue(11)), category, flags),
                    r.IsDBNull(6) ? 0 : Math.Max(0, Convert.ToInt32(r.GetValue(6))),
                    !r.IsDBNull(7) && Convert.ToInt32(r.GetValue(7)) == LandingPoint && (x != 0 || y != 0 || z != 0),
                    x, y, z));
            }
            return new ListedParcelList(list, true);
        }
        catch (MySqlException e) when (e.ErrorCode == MySqlErrorCode.NoSuchTable)
        {
            return new ListedParcelList([], true); // no region has started yet, so OpenSim hasn't made its tables
        }
        catch (Exception e) when (e is MySqlException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return ListedParcelList.Unavailable; // the database is down or still starting: expected, not logged
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var level = Interlocked.Exchange(ref _warned, 1) == 0 ? LogLevel.Warning : LogLevel.Debug;
            log?.Log(level, "The listed places could not be read ({Error}: {Message}).", e.GetType().Name, e.Message);
            return ListedParcelList.Unavailable;
        }
    }

    private static string Text(MySqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i).Trim();

    private static float Float(MySqlDataReader r, int i) => r.IsDBNull(i) ? 0f : Convert.ToSingle(r.GetValue(i));
}
