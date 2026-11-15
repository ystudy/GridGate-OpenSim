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
    public async Task Database_down_is_unavailable()
    {
        var db = await maria.FreshAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var hosts = new RegionHosts(ct => Core.Database.GridDb.OpenRobustAsync(closed, 2, ct));

        await Assert.ThrowsAsync<MoneyUnavailableException>(() => hosts.RegionsAtAsync("192.168.1.20", 0, 65536, Ct));
    }
}
