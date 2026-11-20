using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.GridService;
using OpenSim.Services.Interfaces;
using Xunit;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace GridGate.Platform.Tests;

/// <summary>
/// Robust keeps every region's map tile as a file and never removed one, so after a region moved, its old place kept
/// showing its old picture on every map (seen on the Debian 12 VM: R3 moved from 1003,1003 to 1002,1002). The grid
/// service already notices the move when the region registers at its new place; it now also removes the tiles of the
/// squares the region left.
/// </summary>
public class GridServiceMapTileTests
{
    private static readonly UUID Scope = UUID.Zero;

    [Fact]
    public void A_moved_region_has_its_old_place_tile_removed()
    {
        var (grid, maps) = Make();
        var id = UUID.Random();

        Assert.Equal(string.Empty, grid.RegisterRegion(Scope, Region(id, "R3", 1003, 1003)));
        Assert.Equal(string.Empty, grid.RegisterRegion(Scope, Region(id, "R3", 1002, 1002)));

        Assert.Equal([(1003, 1003)], maps.Removed);
    }

    [Fact]
    public void A_first_registration_or_a_restart_in_place_removes_nothing()
    {
        var (grid, maps) = Make();
        var id = UUID.Random();

        Assert.Equal(string.Empty, grid.RegisterRegion(Scope, Region(id, "Home", 1000, 1000)));
        Assert.Equal(string.Empty, grid.RegisterRegion(Scope, Region(id, "Home", 1000, 1000)));

        Assert.Empty(maps.Removed);
    }

    [Fact]
    public void A_large_region_moving_onto_part_of_its_old_place_keeps_the_squares_it_still_covers()
    {
        var (grid, maps) = Make();
        var id = UUID.Random();

        // 2x2 at 1000,1000 moves one square east: 1000,1000 and 1000,1001 are left; 1001,* are still under it.
        Assert.Equal(string.Empty, grid.RegisterRegion(Scope, Region(id, "Big", 1000, 1000, squares: 2)));
        Assert.Equal(string.Empty, grid.RegisterRegion(Scope, Region(id, "Big", 1001, 1000, squares: 2)));

        Assert.Equal([(1000, 1000), (1000, 1001)], maps.Removed.Order());
    }

    [Fact]
    public void Without_a_map_service_a_move_still_registers()
    {
        var (grid, _) = Make(withMaps: false);
        var id = UUID.Random();

        Assert.Equal(string.Empty, grid.RegisterRegion(Scope, Region(id, "R3", 1003, 1003)));
        Assert.Equal(string.Empty, grid.RegisterRegion(Scope, Region(id, "R3", 1002, 1002)));

        Assert.Equal(1002 * (int)Constants.RegionSize, grid.GetRegionByUUID(Scope, id).RegionLocX);
    }

    private static (TestGridService Grid, RecordingMaps Maps) Make(bool withMaps = true)
    {
        IConfigSource config = new IniConfigSource();
        var section = config.AddConfig("GridService");
        section.Set("StorageProvider", Path.Combine(AppContext.BaseDirectory, "OpenSim.Data.Null.dll"));
        // A store of its own for each test (NullRegionData shares one otherwise).
        section.Set("ConnectionString", "!static");
        var maps = new RecordingMaps();
        var grid = new TestGridService(config);
        if (withMaps) grid.UseMaps(maps);
        return (grid, maps);
    }

    private static GridRegion Region(UUID id, string name, int x, int y, int squares = 1) => new()
    {
        RegionID = id,
        RegionName = name,
        RegionLocX = x * (int)Constants.RegionSize,
        RegionLocY = y * (int)Constants.RegionSize,
        RegionSizeX = squares * (int)Constants.RegionSize,
        RegionSizeY = squares * (int)Constants.RegionSize,
        ExternalHostName = "127.0.0.1",
        HttpPort = 9000,
        InternalEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), 0),
    };

    /// <summary>The grid service with a map service handed in, as Robust's config would load it.</summary>
    public sealed class TestGridService(IConfigSource config) : GridService(config)
    {
        public void UseMaps(IMapImageService maps) => m_MapImageService = maps;
    }

    public sealed class RecordingMaps : IMapImageService
    {
        public List<(int X, int Y)> Removed { get; } = [];

        public bool AddMapTile(int x, int y, byte[] imageData, UUID scopeID, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public bool RemoveMapTile(int x, int y, UUID scopeID, out string reason)
        {
            reason = string.Empty;
            Removed.Add((x, y));
            return true;
        }

        public byte[] GetMapTile(string fileName, UUID scopeID, out string format)
        {
            format = ".jpg";
            return [];
        }
    }
}
