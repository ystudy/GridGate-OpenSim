using GridGate.Launcher.Core.Grid;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Robust keeps a region's size in metres; the map counts in 256 m squares, never fewer than one.</summary>
public sealed class GridRegionsTests
{
    public static TheoryData<object?, int> Sizes => new()
    {
        { 0, 1 }, { 256, 1 }, { 512, 2 }, { 768, 3 }, { DBNull.Value, 1 }, { null, 1 }, { 1024L, 4 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Squares_turns_metres_into_map_squares(object? metres, int squares) => Assert.Equal(squares, GridRegions.Squares(metres));
}
