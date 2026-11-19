using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Money;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// Stories of verified regions and avatars, end to end through the money service over HTTPS. Here (p1) is the Grid Asset
/// Server's own welcome region and There (p2) another of its programs' regions; Neon Pier is on Sam's paired Region Server
/// and Market Square on Jo's.
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
    /// <paramref name="region"/> with Robust, and has its module check in: first sight pins the region as waiting, the owner confirms it
    /// (the row is updated as the Money page's Confirm does), and it is verified.
    /// </summary>
    private static async Task<PairedServer> PairAsync(MoneyServiceRig rig, string host, string key, Guid region)
    {
        var server = new PairedServer(Guid.NewGuid(), host, PairedServer.HashOf(key), DateTimeOffset.UtcNow, 9000, "machine-" + host);
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { PairedServers = [.. rig.Settings.Money.PairedServers ?? [], server] } };
        await rig.Db.AddRegion(region, host, 9000);
        var first = await rig.Post("health", new { protocol = RegionVerification.HandoffProtocol, regions = new[] { region } }, key);
        Assert.Equal(0, first.Body!["regions"]!.GetValue<int>()); // pinned at first sight, waiting for the owner
        await rig.Db.Sql($"UPDATE `money`.`region_pins` SET waiting = 0 WHERE region_id = '{region}'");
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
        // Three residents log in at Here. p2's There claims each of them arrived, though nobody handed them to it. It never
        // becomes their verified place: two keys disagree about where they are, so neither may charge them, and the owner
        // gets one warning for There.
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
            Assert.Equal("unverified_avatar", CodeOf(charge));
            Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Pay(Who(agent, session, secure), Resident(B), 50, Here))));
        }

        Assert.Equal(moved, await Moved(rig)); // no coins moved
        foreach (var (agent, _, _) in people) Assert.Equal(before[agent], await Balance(rig, agent));
        var warning = Assert.Single(await rig.Alerts.OpenAsync(Ct)); // grouped per key and region
        Assert.Equal((AlertKind.UnhandedArrival, AlertLevel.Warning, "p2", There, 3), (warning.Kind, warning.Level, warning.KeyId, warning.RegionId, warning.Count));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task Market_Square_weekend()
    {
        // Sam's Region Server re-registers Market Square, Jo's region, at Sam's own address.
        // The region stays pinned to Jo's server: neither key may take payments there, and the Money page shows the move.
        var rig = await StartAsync();
        var sam = await PairAsync(rig, SamHost, SamKey, NeonPier);
        var jo = await PairAsync(rig, JoHost, JoKey, MarketSquare);
        // A teleports from Here to Market Square and shops there.
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, MarketSquare, toHost: JoHost, toPort: 9000));
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
        // A visits grid H, then comes in through the Gatekeeper straight to Neon Pier, on Sam's Region Server. A Gatekeeper
        // start counts only in the Grid Asset Server's own regions, so A travels, but Neon Pier can't charge them.
        var rig = await StartAsync();
        await PairAsync(rig, SamHost, SamKey, NeonPier);
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, There, "other_grid")); // A leaves for grid H
        await rig.Db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionA}'");
        // The Gatekeeper lets A in at Neon Pier: Presence and a start, then Neon Pier's arrival.
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
        // on to Neon Pier on Sam's Region Server: the handoff carries V's verified place, so V can be paid and can pay there.
        var rig = await StartAsync(); // V was placed at Here by a Gatekeeper start
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { Visitors = VisitorMoney.Listed, ListedGrids = [VisitorHome] } };
        await PairAsync(rig, SamHost, SamKey, NeonPier);

        Assert.Equal("ok", await rig.Handoff(Key, SessionV, Here, NeonPier, toHost: SamHost, toPort: 9000));
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

    [Fact]
    public async Task Through_a_stock_region_and_back()
    {
        // A teleports from Here to Harbour, a region running stock OpenSim that never reports anyone. Here handed A on, so
        // its program can't charge A any more. A then teleports on to There (stock OpenSim hands nobody off): nobody
        // vouches for where A came from, so money stays paused, and nobody is blamed for it.
        var rig = await StartAsync();
        var harbour = Guid.Parse("f4000000-0000-0000-0000-00000000000f");
        var moved = await Moved(rig);

        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, harbour));
        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, Here))));

        Assert.Equal("unverified_avatar", CodeOf(await rig.Arrived(Key2, SessionA, A, There)));
        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, There), Key2)));
        Assert.Equal(moved, await Moved(rig));
        Assert.Empty(await rig.Alerts.OpenAsync(Ct));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task A_move_that_fails_leaves_the_destination_nothing_to_charge()
    {
        // A tries to cross, then to teleport, from Here to There, and both fail: Here's program sends no handoff. There
        // reports the arrival anyway, as a region the viewer touched might. It never becomes A's verified place.
        var rig = await StartAsync();
        var moved = await Moved(rig);

        Assert.Equal("unverified_avatar", CodeOf(await rig.Arrived(Key2, SessionA, A, There)));

        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, There), Key2)));
        Assert.Equal(moved, await Moved(rig));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task A_teleport_to_another_grid_that_fails_keeps_money_on()
    {
        // A starts a teleport to another grid from Here, which hands A off as leaving the grid, and the teleport fails: A
        // is still at Here and can still pay there.
        var rig = await StartAsync();

        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, There, "other_grid"));
        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, Here))));
        Assert.Equal("ok", CodeOf(await rig.Post("handoff/cancel", new { sessionId = SessionA, fromRegion = Here })));

        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, Here))));
        await rig.AssertBooksSoundAsync();
    }

    /// <summary>
    /// A region running stock OpenSim on a server nobody paired, at <see cref="HarbourHost"/>: no key covers it, until a
    /// paired server registers its id at its own address.
    /// </summary>
    private static readonly Guid Harbour = Guid.Parse("f4000000-0000-0000-0000-00000000000f");
    private const string HarbourHost = "192.168.1.77";

    /// <summary>A new resident logs in at <paramref name="region"/>: Robust writes Presence and a login start, with the region's address.</summary>
    private static async Task<(Guid Agent, Guid Session, Guid Secure)> LogInAsync(MoneyServiceRig rig, Guid region)
    {
        var who = (Agent: Guid.NewGuid(), Session: Guid.NewGuid(), Secure: Guid.NewGuid());
        await rig.Db.AddUser(who.Agent);
        await rig.Db.AddPresence(who.Agent, region, who.Session, who.Secure);
        await rig.Db.AddSessionStart(who.Session, who.Agent, region, "login");
        return who;
    }

    private static object Who((Guid Agent, Guid Session, Guid Secure) p) => Who(p.Agent, p.Session, p.Secure);

    /// <summary>Starting balances big enough for a 400-coin payment, and the real Harbour registered at its own address.</summary>
    private static async Task<MoneyServiceRig> WithHarbourAsync(MoneyServiceRig rig)
    {
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { StartingBalancePlay = 1000 } };
        await rig.Db.AddRegion(Harbour, HarbourHost, 9000);
        return rig;
    }

    /// <summary>
    /// Sam reports <paramref name="p"/> arrived in Harbour and tries to charge them 400 coins: refused as unverified, and no
    /// coin moves.
    /// </summary>
    private static async Task AssertSamCannotCharge(MoneyServiceRig rig, (Guid Agent, Guid Session, Guid Secure) p)
    {
        Assert.Equal("unverified_avatar", CodeOf(await rig.Arrived(SamKey, p.Session, p.Agent, Harbour)));
        Assert.Equal(LocationState.Unverified, (await rig.Locations.WhereAsync(p.Session, Ct))?.State);
        // Sam's claim wasn't vouched for, so Sam can't read their balance either.
        Assert.Equal("wrong_region", CodeOf(await rig.Post("balance", Who(p), SamKey)));
        // Their wallet, opened here as the first balance read did.
        await rig.Ledger.BalanceAsync(AccountRef.Resident(p.Agent), Ct);
        var (moved, balance) = (await Moved(rig), await Balance(rig, p.Agent));
        Assert.True(balance >= 400, $"the payer has {balance} coins");

        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Pay(Who(p), Resident(B), 400, Harbour), SamKey)));

        Assert.Equal(moved, await Moved(rig));
        Assert.Equal(balance, await Balance(rig, p.Agent));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task A_server_that_pins_a_region_after_a_login_cannot_charge_that_login()
    {
        // Wendy logs in at Harbour, which no key covers. Robust lets any Region Server register any region id at its own
        // address: Sam's server then lists Harbour, so it is pinned to Sam's server at first sight. Wendy's login went to
        // Harbour's own address, not Sam's, so it isn't Sam's to use: Sam can't verify her, and her coins stay put.
        var rig = await WithHarbourAsync(await StartAsync());
        var wendy = await LogInAsync(rig, Harbour);
        await PairAsync(rig, SamHost, SamKey, Harbour);

        await AssertSamCannotCharge(rig, wendy);
    }

    [Fact]
    public async Task A_server_that_pins_a_region_after_a_handoff_cannot_use_it()
    {
        // A, verified at Here, teleports to Harbour, which no key covers. Moments later Sam's server lists Harbour and pins
        // it. The handoff went to Harbour's own address, so it isn't Sam's to use: A's arrival there verifies nobody.
        var rig = await WithHarbourAsync(await StartAsync());
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, Harbour, toHost: HarbourHost, toPort: 9000));
        await PairAsync(rig, SamHost, SamKey, Harbour);

        await AssertSamCannotCharge(rig, (A, SessionA, SecureA));
    }

    [Fact]
    public async Task A_server_that_pins_a_region_early_cannot_claim_a_login_at_the_real_region()
    {
        // Sam's server registers Harbour's id for a moment, and it is pinned to Sam's server. The real Harbour registers
        // again at its own address; the pin stays. Wendy logs in at the real Harbour. Then Sam registers Harbour again and
        // claims her: her login went to Harbour's address, not Sam's, so it isn't Sam's to use.
        var rig = await WithHarbourAsync(await StartAsync());
        await PairAsync(rig, SamHost, SamKey, Harbour);
        await rig.Db.AddRegion(Harbour, HarbourHost, 9000);
        var wendy = await LogInAsync(rig, Harbour);
        await rig.Db.AddRegion(Harbour, SamHost, 9000);

        await AssertSamCannotCharge(rig, wendy);
    }

    [Fact]
    public async Task A_server_that_pins_a_region_early_cannot_claim_a_handoff_to_the_real_region()
    {
        // As above, but A teleports from Here to the real Harbour while it is back at its own address.
        var rig = await WithHarbourAsync(await StartAsync());
        await PairAsync(rig, SamHost, SamKey, Harbour);
        await rig.Db.AddRegion(Harbour, HarbourHost, 9000);
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, Harbour, toHost: HarbourHost, toPort: 9000));
        await rig.Db.AddRegion(Harbour, SamHost, 9000);

        await AssertSamCannotCharge(rig, (A, SessionA, SecureA));
    }

    [Fact]
    public async Task A_login_after_the_pin_still_verifies()
    {
        // Sam's server covers Harbour, at Sam's address, when Wendy logs in there: her login is Sam's to use.
        var rig = await StartAsync();
        await PairAsync(rig, SamHost, SamKey, Harbour);
        var wendy = await LogInAsync(rig, Harbour);

        Assert.Equal("ok", CodeOf(await rig.Arrived(SamKey, wendy.Session, wendy.Agent, Harbour)));
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Pay(Who(wendy), Resident(B), 10, Harbour), SamKey)));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task A_handoff_after_the_pin_still_verifies()
    {
        // Sam's server covers Harbour, at Sam's address, when A teleports there from Here: the handoff went to Sam's address,
        // so it is Sam's to use.
        var rig = await StartAsync();
        await PairAsync(rig, SamHost, SamKey, Harbour);
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, Harbour, toHost: SamHost, toPort: 9000));

        Assert.Equal("ok", CodeOf(await rig.Arrived(SamKey, SessionA, A, Harbour)));
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, Harbour), SamKey)));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task A_server_paired_long_ago_still_verifies_logins_and_handoffs()
    {
        // Sam's server has covered Harbour for a week: a login there and a handoff there, both to Sam's address, verify.
        var rig = await StartAsync();
        await PairAsync(rig, SamHost, SamKey, Harbour);
        rig.LocationClock.Advance(TimeSpan.FromDays(7));
        var wendy = await LogInAsync(rig, Harbour);
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, Harbour, toHost: SamHost, toPort: 9000));

        Assert.Equal("ok", CodeOf(await rig.Arrived(SamKey, wendy.Session, wendy.Agent, Harbour)));
        Assert.Equal("ok", CodeOf(await rig.Arrived(SamKey, SessionA, A, Harbour)));
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Pay(Who(wendy), Resident(B), 10, Harbour), SamKey)));
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Pay(WhoA(), Resident(B), 10, Harbour), SamKey)));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task A_handoff_reported_after_the_region_was_registered_again_cannot_be_used()
    {
        // A, verified at Here, teleports to Harbour at its own address. Here's module reports the handoff only once the
        // teleport has finished, and it may wait in the queue a while. Before it lands, Sam's server registers Harbour at
        // Sam's address, pins it and reports A's arrival. The handoff says where A really went, Harbour's own address: it
        // isn't Sam's to use, whatever Robust lists Harbour at by now.
        var rig = await WithHarbourAsync(await StartAsync());
        await PairAsync(rig, SamHost, SamKey, Harbour);
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, Harbour, toHost: HarbourHost, toPort: 9000));

        await AssertSamCannotCharge(rig, (A, SessionA, SecureA));
    }

    [Fact]
    public async Task A_handoff_with_no_address_counts_for_no_paired_server()
    {
        // An older module sends no address: nothing says A went to Sam's server, so Sam can't use the handoff.
        var rig = await WithHarbourAsync(await StartAsync());
        await PairAsync(rig, SamHost, SamKey, Harbour);
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, Harbour));

        await AssertSamCannotCharge(rig, (A, SessionA, SecureA));
    }
}
