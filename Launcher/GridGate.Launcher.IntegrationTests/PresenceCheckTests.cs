using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Checking a session against Robust's Presence table (money plan 1, task 7).</summary>
[Collection(GridCollection.Name)]
public sealed class PresenceCheckTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid User = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Here = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly Guid There = Guid.Parse("f0000000-0000-0000-0000-00000000000f");
    private static readonly Guid Session = Guid.Parse("50000000-0000-0000-0000-000000000005");
    private static readonly Guid Secure = Guid.Parse("60000000-0000-0000-0000-000000000006");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<PresenceCheck> Setup(Guid region)
    {
        var db = await maria.FreshAsync();
        await db.AddPresence(User, region, Session, Secure);
        await db.AddUser(User);
        return new PresenceCheck(db.OpenRobust);
    }

    [Fact]
    public async Task Zero_session_is_NoSession()
    {
        var check = await Setup(Here);

        Assert.Equal(PresenceOutcome.NoSession, await check.LoggedInAsync(User, Guid.Empty, Secure, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.LoggedInAsync(User, Session, Guid.Empty, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.LoggedInAsync(Guid.Empty, Session, Secure, Ct));
    }

    [Fact]
    public async Task Logged_in_in_any_region_is_Ok()
    {
        var check = await Setup(There); // Presence.RegionID is no longer read (money design §8.4)

        Assert.Equal(PresenceOutcome.Ok, await check.LoggedInAsync(User, Session, Secure, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.LoggedInAsync(User, Session, Guid.NewGuid(), Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.LoggedInAsync(User, Guid.NewGuid(), Secure, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.LoggedInAsync(Guid.NewGuid(), Session, Secure, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.LoggedInAsync(User, Session, Guid.Empty, Ct));
    }

    [Fact]
    public async Task Session_without_secure_session_is_Ok()
    {
        var check = await Setup(There);

        Assert.Equal(PresenceOutcome.Ok, await check.HasSessionAsync(User, Session, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.HasSessionAsync(User, Guid.NewGuid(), Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.HasSessionAsync(Guid.NewGuid(), Session, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.HasSessionAsync(User, Guid.Empty, Ct));
    }

    [Fact]
    public async Task Unreachable_robust_is_Unavailable_when_logged_in_is_asked()
    {
        var db = await maria.FreshAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var check = new PresenceCheck(ct => Core.Database.GridDb.OpenRobustAsync(closed, 2, ct));

        Assert.Equal(PresenceOutcome.Unavailable, await check.LoggedInAsync(User, Session, Secure, Ct));
        Assert.Equal(PresenceOutcome.Unavailable, await check.HasSessionAsync(User, Session, Ct));
    }

    [Fact]
    public async Task Secure_session_alone_is_ok()
    {
        var check = await Setup(There); // any region: the helper has no region of its own

        Assert.Equal(PresenceOutcome.Ok, await check.HasSecureSessionAsync(User, Secure, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.HasSecureSessionAsync(User, Guid.NewGuid(), Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.HasSecureSessionAsync(User, Guid.Empty, Ct));
        Assert.Equal(PresenceOutcome.NoSession, await check.HasSecureSessionAsync(Guid.NewGuid(), Secure, Ct));
    }

    [Fact]
    public async Task Visitor_presence_row_counts()
    {
        var db = await maria.FreshAsync();
        var visitor = Guid.NewGuid();
        await db.AddPresence(visitor, Here, Session, Secure); // the Gatekeeper's LoginAgent row: no UserAccounts row
        var check = new PresenceCheck(db.OpenRobust);

        Assert.Equal(PresenceOutcome.Ok, await check.LoggedInAsync(visitor, Session, Secure, Ct));
        Assert.False(await check.IsLocalUserAsync(visitor, Ct));
    }

    [Fact]
    public async Task IsLocalUser_reads_UserAccounts()
    {
        var check = await Setup(Here);

        Assert.True(await check.IsLocalUserAsync(User, Ct));
        Assert.False(await check.IsLocalUserAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Gatekeeper_row_makes_a_known_visitor()
    {
        var db = await maria.FreshAsync();
        var visitor = Guid.NewGuid();
        await db.AddVisitor(visitor, "http://grid.example:8002/");
        var check = new PresenceCheck(db.OpenRobust);

        Assert.True(await check.IsKnownVisitorAsync(visitor, "http://GRID.example:8002", Ct));
        Assert.True(await check.IsKnownVisitorAsync(visitor, " http://grid.example:8002/ ", Ct));
    }

    [Fact]
    public async Task Other_home_or_other_id_is_not_known()
    {
        var db = await maria.FreshAsync();
        var visitor = Guid.NewGuid();
        await db.AddVisitor(visitor, "http://grid.example:8002/");
        await db.Sql($"INSERT INTO `robust`.`GridUser` (`UserID`) VALUES ('{User}')"); // a resident's row: no home part
        var check = new PresenceCheck(db.OpenRobust);

        Assert.False(await check.IsKnownVisitorAsync(visitor, "http://other.example/", Ct));
        Assert.False(await check.IsKnownVisitorAsync(Guid.NewGuid(), "http://grid.example:8002/", Ct));
        Assert.False(await check.IsKnownVisitorAsync(User, "", Ct));
        Assert.False(await check.IsKnownVisitorAsync(visitor, "http://grid.example", Ct)); // a different port is a different grid
    }

    [Fact]
    public async Task The_first_home_seen_wins()
    {
        var db = await maria.FreshAsync();
        var visitor = Guid.NewGuid();
        await db.AddVisitor(visitor, "http://old.example:8002/");
        await db.AddVisitor(visitor, "http://new.example:8002/"); // OpenSim updates the first row instead
        var check = new PresenceCheck(db.OpenRobust);

        Assert.True(await check.IsKnownVisitorAsync(visitor, "http://old.example:8002", Ct));
        Assert.False(await check.IsKnownVisitorAsync(visitor, "http://new.example:8002", Ct));
    }

    [Fact]
    public async Task Empty_home_is_not_known_without_a_query()
    {
        var check = new PresenceCheck(_ => throw new InvalidOperationException("no query expected"));

        Assert.False(await check.IsKnownVisitorAsync(Guid.NewGuid(), "  ", Ct));
    }
}
