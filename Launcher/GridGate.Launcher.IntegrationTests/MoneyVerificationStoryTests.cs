using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Money;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The verified-regions design's stories, end to end through the money service over HTTPS (design §12, "The Neon Pier and
/// Market Square stories end to end, as tests"). Here (p1) is the Grid Asset Server's own welcome region and There (p2)
/// another of its programs' regions; Neon Pier is on Sam's paired Region Server and Market Square on Jo's.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyVerificationStoryTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private static readonly Guid NeonPier = Guid.Parse("f2000000-0000-0000-0000-00000000000f");
    private static readonly Guid MarketSquare = Guid.Parse("f3000000-0000-0000-0000-00000000000f");
    private const string SamHost = "192.168.1.20";
    private const string JoHost = "192.168.1.30";
    private const string SamKey = "the-region-key-for-sams-region-server-000000000";
    private const string JoKey = "the-region-key-for-jos-region-server-0000000000";

    private MoneyServiceRig? _rig;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null) await _rig.DisposeAsync();
    }

    private async Task<MoneyServiceRig> StartAsync(bool place = true) => _rig = await MoneyServiceRig.StartAsync(maria, place);

    /// <summary>
    /// Pairs a Region Server at <paramref name="host"/> whose key is <paramref name="key"/>, registers its region
    /// <paramref name="region"/> with Robust, and has its module check in: first sight pins the region, and it is verified.
    /// </summary>
    private static async Task<PairedServer> PairAsync(MoneyServiceRig rig, string host, string key, Guid region)
    {
        var server = new PairedServer(Guid.NewGuid(), host, PairedServer.HashOf(key), DateTimeOffset.UtcNow, 9000, "machine-" + host);
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { PairedServers = [.. rig.Settings.Money.PairedServers ?? [], server] } };
        await rig.Db.AddRegion(region, host, 9000);
        var health = await rig.Post("health", new { protocol = RegionVerification.HandoffProtocol, regions = new[] { region } }, key);
        Assert.Equal(1, health.Body!["regions"]!.GetValue<int>());
        return server;
    }

    private static object Resident(Guid id) => new { agentId = id, home = "" };

    private static object Who(Guid agent, Guid session, Guid secure) => new { agentId = agent, home = "", sessionId = session, secureSessionId = secure };

    private static object Pay(object from, object to, long amount, Guid region) =>
        new { txnId = Guid.NewGuid(), type = "pay_avatar", from, to, amount, regionId = region };

    /// <summary>Entries other than starting balances, plus holds: anything a region moved.</summary>
    private static Task<long> Moved(MoneyServiceRig rig) =>
        rig.Db.Scalar("SELECT (SELECT COUNT(*) FROM `money`.`entries` WHERE type <> 'starting_balance') + (SELECT COUNT(*) FROM `money`.`holds`)");

    private static Task<long> Balance(MoneyServiceRig rig, Guid agent) =>
        rig.Db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{agent}'");

    [Fact]
    public async Task Neon_Pier_night()
    {
        // Three residents log in at Here. p2's There saw their session ids next door and claims each of them arrived, so it
        // can charge them (verified-regions design §1, §8.4). It never becomes their verified place: two keys disagree about
        // where they are, so neither may charge them, and the owner gets one warning for There.
        var rig = await StartAsync(place: false);
        await rig.Health(Key);
        await rig.Health(Key2);
        var people = new List<(Guid Agent, Guid Session, Guid Secure)> { (A, SessionA, SecureA) };
        for (var i = 0; i < 2; i++)
        {
            var person = (Agent: Guid.NewGuid(), Session: Guid.NewGuid(), Secure: Guid.NewGuid());
            await rig.Db.AddUser(person.Agent);
            await rig.Db.AddPresence(person.Agent, Here, person.Session, person.Secure);
            people.Add(person);
        }
        var before = new Dictionary<Guid, long>();
        foreach (var (agent, session, secure) in people)
        {
            Assert.Equal("ok", await rig.Place(session, Here, agent: agent)); // verified at Here
            Assert.Equal("ok", CodeOf(await rig.Post("balance", Who(agent, session, secure))));
            before[agent] = await Balance(rig, agent);
        }
        var moved = await Moved(rig);

        foreach (var (agent, session, secure) in people)
        {
            Assert.Equal("unverified_avatar", CodeOf(await rig.Arrived(Key2, session, agent, There)));
            var charge = await rig.Post("transfer", Pay(Who(agent, session, secure), Resident(B), 50, There), Key2);
            Assert.True(CodeOf(charge) is "unverified_avatar" or "wrong_region", charge.Raw);
            Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Pay(Who(agent, session, secure), Resident(B), 50, Here))));
        }

        Assert.Equal(moved, await Moved(rig)); // no coins moved
        foreach (var (agent, _, _) in people) Assert.Equal(before[agent], await Balance(rig, agent));
        var warning = Assert.Single(await rig.Alerts.OpenAsync(Ct)); // grouped per key and region (Ruling 2)
        Assert.Equal((AlertKind.UnhandedArrival, AlertLevel.Warning, "p2", There, 3), (warning.Kind, warning.Level, warning.KeyId, warning.RegionId, warning.Count));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task Market_Square_weekend()
    {
        // Sam's Region Server re-registers Market Square, Jo's region, at Sam's own address (verified-regions design §8.3, L-4).
        // The region stays pinned to Jo's server: neither key may take payments there, and the Money page shows the move.
        var rig = await StartAsync();
        var sam = await PairAsync(rig, SamHost, SamKey, NeonPier);
        var jo = await PairAsync(rig, JoHost, JoKey, MarketSquare);
        // A teleports from Here to Market Square and shops there.
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, MarketSquare));
        Assert.Equal("ok", CodeOf(await rig.Arrived(JoKey, SessionA, A, MarketSquare)));
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, MarketSquare), JoKey)));

        await rig.Db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{SamHost}' WHERE uuid = '{MarketSquare}'");

        var moved = await Moved(rig);
        var balance = await Balance(rig, A);
        Assert.Equal("wrong_region", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, MarketSquare), SamKey)));
        Assert.Equal("wrong_region", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, MarketSquare), JoKey)));
        Assert.Equal(moved, await Moved(rig));
        Assert.Equal(balance, await Balance(rig, A));

        // The Money page's Regions list reads these states (IMoneyRegions.AllAsync) and words them with MoneyVerifyText.
        var state = (await rig.Verification.AllAsync(rig.Settings, Ct)).Single(s => s.RegionId == MarketSquare);
        Assert.Equal((RegionStatus.Moved, MoneyVerifyText.KeyOf(sam), MoneyVerifyText.KeyOf(jo)), (state.Status, state.KeyId, state.PinnedKeyId));
        Assert.Equal($"Moved here from Region Server at {JoHost} (ports from 9000), waiting for you",
            MoneyVerifyText.Words(state.Status, MoneyVerifyText.KeyName(state.PinnedKeyId!, rig.Settings)));
        var alert = Assert.Single(await rig.Alerts.OpenAsync(Ct));
        Assert.Equal((AlertKind.RegionMoved, MarketSquare, MoneyVerifyText.KeyOf(sam)), (alert.Kind, alert.RegionId, alert.KeyId));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task Grid_H_sends_our_resident_into_a_Region_Server()
    {
        // A visits grid H. While A is still away, grid H asks our Gatekeeper to send A to Neon Pier, on Sam's Region Server,
        // and Neon Pier reports the arrival (verified-regions design §8.4, the Gatekeeper rule). A Gatekeeper start counts
        // only in the Grid Asset Server's own regions, so A travels, but Neon Pier can't charge them.
        var rig = await StartAsync();
        await PairAsync(rig, SamHost, SamKey, NeonPier);
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, There, "other_grid")); // A leaves for grid H
        await rig.Db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionA}'");
        // The Gatekeeper lets A in at Neon Pier, as grid H asked: Presence and a start, then Neon Pier's arrival.
        await rig.Db.AddPresence(A, NeonPier, SessionA, SecureA);
        Assert.Equal("unverified_avatar", await rig.Place(SessionA, NeonPier, "gatekeeper", SamKey));
        var moved = await Moved(rig);

        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, NeonPier), SamKey)));
        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("hold",
            new { txnId = Guid.NewGuid(), kind = "object", buyer = WhoA(), seller = Resident(B), amount = 10, regionId = NeonPier, objectId = Guid.NewGuid(), objectName = "dress" }, SamKey)));
        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("charge",
            new { txnId = Guid.NewGuid(), type = "upload_fee", payer = WhoA(), amount = 3, regionId = NeonPier }, SamKey)));
        Assert.Equal(moved, await Moved(rig));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task Visitor_from_listed_grid_through_local_welcome_then_teleport_on_pays()
    {
        // V comes from a listed grid through the Gatekeeper into Here, the Grid Asset Server's welcome region, then teleports
        // on to Neon Pier on Sam's Region Server: the handoff carries V's verified place, so V can be paid and can pay there
        // (verified-regions design §8.4, "What people notice", and §8.6).
        var rig = await StartAsync(); // V was placed at Here by a Gatekeeper start
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { Visitors = VisitorMoney.Listed, ListedGrids = [VisitorHome] } };
        await PairAsync(rig, SamHost, SamKey, NeonPier);

        Assert.Equal("ok", await rig.Handoff(Key, SessionV, Here, NeonPier));
        Assert.Equal("ok", CodeOf(await rig.Arrived(SamKey, SessionV, V, NeonPier)));
        // B tips V at There; V spends some of it at Neon Pier.
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Pay(WhoB(), new { agentId = V, home = VisitorHome }, 50, There), Key2)));
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Pay(WhoV(), Resident(A), 20, NeonPier), SamKey)));
        Assert.Equal(30, await rig.Db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{V}' AND home <> ''"));

        // Only the listing lets V use money: with V's grid off the list, the same payment is refused and the coins stay.
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { ListedGrids = [] } };
        Assert.Equal("visitor_grid_not_listed", CodeOf(await rig.Post("transfer", Pay(WhoV(), Resident(A), 20, NeonPier), SamKey)));
        Assert.Equal(30, await rig.Db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{V}' AND home <> ''"));
        await rig.AssertBooksSoundAsync();
    }
}
