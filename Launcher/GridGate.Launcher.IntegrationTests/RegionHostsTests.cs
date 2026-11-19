using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Which regions Robust lists at an address (money plan 3, task 5).</summary>
[Collection(GridCollection.Name)]
public sealed class RegionHostsTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Regions_at_an_address()
    {
        var db = await maria.FreshAsync();
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.AddRegion(a, "192.168.1.20");
        await db.AddRegion(b, "192.168.1.20");
        await db.AddRegion(c, "192.168.1.30");
        var hosts = new RegionHosts(db.OpenRobust);

        Assert.Equal(new HashSet<Guid> { a, b }, (await hosts.RegionsAtAsync("192.168.1.20", 0, 65536, Ct)).ToHashSet());
        Assert.Empty(await hosts.RegionsAtAsync("192.168.1.99", 0, 65536, Ct));
    }

    [Fact]
    public async Task Coverage_and_events_use_one_comparison()
    {
        // Hosts ignore case; ports run from the first up to, not including, the next server's first port. Nothing missing
        // is at any server.
        Assert.True(RegionHosts.IsAt("Region.Example", 9001, "region.example", 9000, 9100));
        Assert.False(RegionHosts.IsAt("region.example", 9100, "region.example", 9000, 9100));
        Assert.False(RegionHosts.IsAt("region.example", 8999, "region.example", 9000, 9100));
        Assert.False(RegionHosts.IsAt(null, 9001, "region.example", 9000, 9100));
        Assert.False(RegionHosts.IsAt("region.example", null, "region.example", 9000, 9100));

        // RegionsAtAsync lists exactly the regions IsAt puts at the server.
        var db = await maria.FreshAsync();
        var (mixed, spaced, outside) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.AddRegion(mixed, "Region.Example", 9001);
        await db.AddRegion(spaced, "region.example ", 9002);
        await db.AddRegion(outside, "region.example", 9100);
        var found = await new RegionHosts(db.OpenRobust).RegionsAtAsync("region.example", 9000, 9100, Ct);
        Assert.Equal(new HashSet<Guid> { mixed }, found.ToHashSet());
    }

    [Fact]
    public async Task Database_down_is_unavailable()
    {
        var db = await maria.FreshAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var hosts = new RegionHosts(ct => Core.Database.GridDb.OpenRobustAsync(closed, 2, ct));

        await Assert.ThrowsAsync<MoneyUnavailableException>(() => hosts.RegionsAtAsync("192.168.1.20", 0, 65536, Ct));
    }
}
