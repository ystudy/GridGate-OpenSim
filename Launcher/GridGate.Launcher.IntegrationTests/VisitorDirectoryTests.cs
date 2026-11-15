using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Finding hypergrid visitors in Robust's GridUser table for the Money pages (money plan 3, task 3).</summary>
[Collection(GridCollection.Name)]
public sealed class VisitorDirectoryTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Search_finds_gatekeeper_rows_by_name()
    {
        var db = await maria.FreshAsync();
        var hope = Guid.NewGuid();
        var older = Guid.NewGuid();
        await db.AddVisitor(hope, "http://grid.example:8002/", "Hope Visitor", login: 2000);
        await db.AddVisitor(older, "http://old.example:8002/", "Hope Older", login: 1000);
        await db.Sql($"INSERT INTO `robust`.`GridUser` (`UserID`) VALUES ('{Guid.NewGuid()}')"); // a resident: no home, no name
        var directory = new VisitorDirectory(db.OpenRobust);

        var found = await directory.SearchAsync("hope", 10, Ct);

        Assert.Equal([new VisitorInfo(hope, "http://grid.example:8002/", "Hope Visitor"), new VisitorInfo(older, "http://old.example:8002/", "Hope Older")], found);
        Assert.Single(await directory.SearchAsync("hope", 1, Ct));
        Assert.Empty(await directory.SearchAsync("nobody", 10, Ct));
    }

    [Fact]
    public async Task Search_escapes_like_wildcards()
    {
        var db = await maria.FreshAsync();
        await db.AddVisitor(Guid.NewGuid(), "http://grid.example:8002/", "Hope Visitor");
        var directory = new VisitorDirectory(db.OpenRobust);

        Assert.Empty(await directory.SearchAsync("%", 10, Ct));
        Assert.Empty(await directory.SearchAsync("H_pe", 10, Ct));
    }

    [Fact]
    public async Task Find_matches_home_without_case_or_slash()
    {
        var db = await maria.FreshAsync();
        var hope = Guid.NewGuid();
        await db.AddVisitor(hope, "http://grid.example:8002/", "Hope Visitor");
        var directory = new VisitorDirectory(db.OpenRobust);

        Assert.Equal("Hope Visitor", (await directory.FindAsync(hope, "HTTP://Grid.Example:8002", Ct))?.Name);
        Assert.Null(await directory.FindAsync(hope, "http://other.example/", Ct));
        Assert.Null(await directory.FindAsync(Guid.NewGuid(), "http://grid.example:8002/", Ct));
    }

    [Fact]
    public async Task Database_down_is_unavailable()
    {
        var db = await maria.FreshAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var directory = new VisitorDirectory(ct => Core.Database.GridDb.OpenRobustAsync(closed, 2, ct));

        await Assert.ThrowsAsync<MoneyUnavailableException>(() => directory.SearchAsync("hope", 10, Ct));
    }
}
