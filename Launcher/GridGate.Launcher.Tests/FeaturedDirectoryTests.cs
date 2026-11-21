using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Featured;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The directory the Featured page reads (spec 2026-11-21-featured-places-design, "Which places show"): listed parcels on
/// this grid's online regions only, built at most once a minute and shared.
/// </summary>
public sealed class FeaturedDirectoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly GridRegion Link = new(Guid.Parse("0f000000-0000-0000-0000-0000000000d3"), "Other grid", 2000, 2000, IsLink: true);
    private static readonly Guid Gone = Guid.Parse("0f000000-0000-0000-0000-0000000000e4");

    [Fact]
    public async Task Only_online_own_regions()
    {
        var listed = new FakeListedParcels
        {
            Result = new([
                FeaturedQueryTests.Parcel(1, FeaturedQueryTests.Bay, "Beach shop"),
                FeaturedQueryTests.Parcel(2, Link, "Linked place"),
                FeaturedQueryTests.Parcel(3, FeaturedQueryTests.Bay, "Gone place") with { RegionId = Gone },
            ], true),
        };
        var regions = new FakeGridRegions { Result = new([FeaturedQueryTests.Bay, Link], true) };

        var data = await new FeaturedDirectory(listed, regions, new FakeTimeProvider()).CurrentAsync(Ct);

        Assert.True(data.Available);
        var place = Assert.Single(data.Places);
        Assert.Equal("Beach shop", place.Parcel.Name);
        Assert.Equal("Sunny Bay", place.Region.Name);
    }

    [Fact]
    public async Task Built_once_a_minute()
    {
        var listed = new FakeListedParcels();
        var time = new FakeTimeProvider();
        var directory = new FeaturedDirectory(listed, new FakeGridRegions(), time);

        await directory.CurrentAsync(Ct);
        await directory.CurrentAsync(Ct);
        Assert.Equal(1, listed.Calls);

        time.Advance(FeaturedDirectory.Lifetime + TimeSpan.FromSeconds(1));
        await directory.CurrentAsync(Ct);
        Assert.Equal(2, listed.Calls);
    }

    [Fact]
    public async Task Unavailable_when_regions_or_parcels_unavailable()
    {
        var regionsDown = new FeaturedDirectory(new FakeListedParcels(), new FakeGridRegions { Result = GridRegionList.Unavailable },
            new FakeTimeProvider());
        var parcelsDown = new FeaturedDirectory(new FakeListedParcels { Result = ListedParcelList.Unavailable }, new FakeGridRegions(),
            new FakeTimeProvider());

        Assert.False((await regionsDown.CurrentAsync(Ct)).Available);
        Assert.False((await parcelsDown.CurrentAsync(Ct)).Available);
    }
}

/// <summary>The listed parcels without a database.</summary>
internal sealed class FakeListedParcels : IListedParcels
{
    public ListedParcelList Result { get; set; } = new([], true);
    public int Calls { get; private set; }

    public Task<ListedParcelList> ListAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
