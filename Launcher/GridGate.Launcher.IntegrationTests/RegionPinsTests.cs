using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>The owner confirming everything a test server has waiting and listed.</summary>
internal static class RegionPinsTestExtensions
{
    public static Task<int> ConfirmAllAsync(this RegionPins pins, PairedServer server, CancellationToken ct) =>
        pins.ConfirmWaitingAsync(server, server.FirstPort, 65536, new HashSet<Guid>(), ct);
}

/// <summary>Region ids belong to the paired Region Server they were first seen under.</summary>
[Collection(GridCollection.Name)]
public sealed class RegionPinsTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private const string HostA = "192.168.1.20";
    private const string HostB = "192.168.1.30";
    private static readonly IReadOnlySet<Guid> NoOwn = new HashSet<Guid>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PairedServer Server(string host, string? machine = null, int firstPort = 9000) =>
        new(Guid.NewGuid(), host, new string('a', 64), DateTimeOffset.UtcNow, firstPort, machine);

    private static async Task<(MoneyTestDatabase Db, RegionPins Pins)> StartAsync(MoneyMariaDb maria)
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        return (db, new RegionPins(db.OpenMoney, new RegionHosts(db.OpenRobust), TimeProvider.System));
    }

    private static Task<IReadOnlySet<Guid>> Covered(RegionPins pins, PairedServer s) => pins.CoveredAsync(s, s.FirstPort, 65536, NoOwn, Ct);

    [Fact]
    public async Task First_sight_pins_to_the_server()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, region) = (Server(HostA, "m-a"), Guid.NewGuid());
        await db.AddRegion(region, HostA);

        Assert.Equal(new HashSet<Guid> { region }, (await Covered(pins, a)).ToHashSet());

        Assert.Equal(a.Id.ToString("N"), await db.Text($"SELECT server_id FROM `money`.`region_pins` WHERE region_id = '{region}'"));
        Assert.Equal("m-a", await db.Text($"SELECT machine_id FROM `money`.`region_pins` WHERE region_id = '{region}'"));
    }

    [Fact]
    public async Task Own_regions_are_never_pinned()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, own) = (Server(HostA), Guid.NewGuid());
        await db.AddRegion(own, HostA);

        Assert.Empty(await pins.CoveredAsync(a, 9000, 65536, new HashSet<Guid> { own }, Ct));

        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`region_pins`"));
    }

    [Fact]
    public async Task Region_listed_at_A_but_pinned_to_B_is_covered_by_neither_and_listed_as_a_move()
    {
        // The Market Square story: B's region is re-registered by A's program.
        var (db, pins) = await StartAsync(maria);
        var (a, b, region) = (Server(HostA), Server(HostB), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        Assert.Contains(region, await Covered(pins, b));

        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");

        Assert.Empty(await Covered(pins, a));
        Assert.Empty(await Covered(pins, b));
        Assert.Equal(b.Id.ToString("N"), await db.Text($"SELECT server_id FROM `money`.`region_pins` WHERE region_id = '{region}'"));
        Assert.Equal([new RegionMove(region, b.Id, a.Id)], await pins.MovesAsync([a, b], Ct));
    }

    [Fact]
    public async Task Approving_a_move_repins_it()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, b, region) = (Server(HostA), Server(HostB), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, b);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");

        await pins.ApproveMoveAsync(region, a.Id, Ct);

        Assert.Equal(new HashSet<Guid> { region }, (await Covered(pins, a)).ToHashSet());
        Assert.Empty(await pins.MovesAsync([a, b], Ct));
    }

    [Fact]
    public async Task Approving_a_move_leaves_nobody_verified_in_the_region()
    {
        // People B placed in the region stay B's to charge only if nothing changes: the server that now runs it, A, never
        // vouched for them, so money pauses for them until their next login. Elsewhere nobody is touched.
        var (db, pins) = await StartAsync(maria);
        var (a, b, region, other) = (Server(HostA), Server(HostB), Guid.NewGuid(), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, b);
        var (here, handed, elsewhere) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Sql($"""
            INSERT INTO `money`.`session_locations` (session_id, agent_id, region_id, key_id, state, last_start_id, updated) VALUES
              ('{here}', '{Guid.NewGuid()}', '{region}', 'server:{b.Id:N}', 'Verified', 1, UTC_TIMESTAMP(6)),
              ('{handed}', '{Guid.NewGuid()}', '{region}', 'p1', 'Handed', 1, UTC_TIMESTAMP(6)),
              ('{elsewhere}', '{Guid.NewGuid()}', '{other}', 'server:{b.Id:N}', 'Verified', 1, UTC_TIMESTAMP(6))
            """);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");

        await pins.ApproveMoveAsync(region, a.Id, Ct);

        Assert.Equal("Unverified", await db.Text($"SELECT state FROM `money`.`session_locations` WHERE session_id = '{here}'"));
        Assert.Equal("Unverified", await db.Text($"SELECT state FROM `money`.`session_locations` WHERE session_id = '{handed}'"));
        Assert.Equal("Verified", await db.Text($"SELECT state FROM `money`.`session_locations` WHERE session_id = '{elsewhere}'"));
    }

    [Fact]
    public async Task Keeping_off_leaves_it_with_B_and_B_gets_it_back_when_it_registers_again()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, b, region) = (Server(HostA), Server(HostB), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, b);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");
        Assert.Empty(await Covered(pins, a));

        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostB}' WHERE uuid = '{region}'");

        Assert.Equal(new HashSet<Guid> { region }, (await Covered(pins, b)).ToHashSet());
        Assert.Empty(await Covered(pins, a));
        Assert.Empty(await pins.MovesAsync([a, b], Ct));
    }

    [Fact]
    public async Task A_region_that_leaves_Robust_stays_pinned()
    {
        var (db, pins) = await StartAsync(maria);
        var (b, region) = (Server(HostB), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, b);

        await db.Sql($"DELETE FROM `robust`.`regions` WHERE uuid = '{region}'");

        Assert.Empty(await Covered(pins, b));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`region_pins`"));
    }

    [Fact]
    public async Task Removing_a_server_keeps_its_pins_so_another_server_listing_them_sees_a_move()
    {
        // Nothing deletes a pin when its server is removed: the region stays Jo's, so Sam listing it is a move for the owner.
        var (db, pins) = await StartAsync(maria);
        var (jo, sam, region) = (Server(HostB), Server(HostA), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, jo);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");

        Assert.Empty(await Covered(pins, sam));
        Assert.Equal([new RegionMove(region, jo.Id, sam.Id)], await pins.MovesAsync([sam], Ct));
        Assert.Equal(jo.Id.ToString("N"), await db.Text($"SELECT server_id FROM `money`.`region_pins` WHERE region_id = '{region}'"));
    }

    [Fact]
    public async Task Regions_listed_when_a_server_pairs_wait_and_cover_nothing()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, r1, r2) = (Server(HostA), Guid.NewGuid(), Guid.NewGuid());
        await db.AddRegion(r1, HostA);
        await db.AddRegion(r2, HostA);

        Assert.Equal(2, await pins.PinWaitingAsync(a, a.FirstPort, 65536, Ct));

        Assert.Empty(await Covered(pins, a));
        Assert.Equal(2, await db.Scalar($"SELECT COUNT(*) FROM `money`.`region_pins` WHERE server_id = '{a.Id:N}' AND waiting = 1"));
        Assert.Equal([r1, r2], (await pins.WaitingAsync(a.Id, Ct)).Order());
        Assert.Empty(await pins.MovesAsync([a], Ct));
    }

    [Fact]
    public async Task Confirming_waiting_regions_makes_them_covered()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, region) = (Server(HostA), Guid.NewGuid());
        await db.AddRegion(region, HostA);
        await pins.PinWaitingAsync(a, a.FirstPort, 65536, Ct);

        Assert.Equal(1, await pins.ConfirmAllAsync(a, Ct));

        Assert.Equal(new HashSet<Guid> { region }, (await Covered(pins, a)).ToHashSet());
        Assert.Empty(await pins.WaitingAsync(a.Id, Ct));
    }

    [Fact]
    public async Task Confirming_covers_only_the_waiting_regions_that_are_listed_at_the_server()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, shown, unlisted) = (Server(HostA), Guid.NewGuid(), Guid.NewGuid());
        await db.AddRegion(shown, HostA);
        await db.AddRegion(unlisted, HostA);
        await pins.PinWaitingAsync(a, a.FirstPort, 65536, Ct);
        await db.Sql($"DELETE FROM `robust`.`regions` WHERE uuid = '{unlisted}'");

        Assert.Equal([shown], await pins.WaitingListedAsync(a, a.FirstPort, 65536, NoOwn, Ct));
        Assert.Equal(1, await pins.ConfirmAllAsync(a, Ct));

        Assert.Equal(new HashSet<Guid> { shown }, (await Covered(pins, a)).ToHashSet());
        Assert.Equal([unlisted], await pins.WaitingAsync(a.Id, Ct));
        Assert.Equal(0, await pins.ConfirmAllAsync(a, Ct));
    }

    [Fact]
    public async Task Approving_a_move_turns_a_waiting_region_on_and_leaves_the_servers_other_waiting_regions_waiting()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, b, moved, other) = (Server(HostA), Server(HostB), Guid.NewGuid(), Guid.NewGuid());
        await db.AddRegion(moved, HostA);
        await db.AddRegion(other, HostB);
        await pins.PinWaitingAsync(a, a.FirstPort, 65536, Ct);
        await pins.PinWaitingAsync(b, b.FirstPort, 65536, Ct);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostB}' WHERE uuid = '{moved}'");

        await pins.ApproveMoveAsync(moved, b.Id, Ct);

        Assert.Equal(new HashSet<Guid> { moved }, (await Covered(pins, b)).ToHashSet());
        Assert.Equal([other], await pins.WaitingAsync(b.Id, Ct));
    }

    [Fact]
    public async Task A_status_read_pins_nothing_but_the_request_path_does()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, region) = (Server(HostA), Guid.NewGuid());
        await db.AddRegion(region, HostA);

        Assert.Equal(new HashSet<Guid> { region }, (await pins.CoveredAsync(a, a.FirstPort, 65536, NoOwn, Ct, pinNew: false)).ToHashSet());
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`region_pins`"));

        await pins.CoveredAsync(a, a.FirstPort, 65536, NoOwn, Ct);
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`region_pins`"));
    }

    [Fact]
    public async Task A_region_that_appears_after_pairing_is_pinned_and_covered_at_first_sight()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, early, late) = (Server(HostA), Guid.NewGuid(), Guid.NewGuid());
        await db.AddRegion(early, HostA);
        await pins.PinWaitingAsync(a, a.FirstPort, 65536, Ct);
        await db.AddRegion(late, HostA);

        Assert.Equal(new HashSet<Guid> { late }, (await Covered(pins, a)).ToHashSet());
        Assert.Equal([early], await pins.WaitingAsync(a.Id, Ct));
    }

    [Fact]
    public async Task A_region_waiting_for_another_server_moves_to_the_server_whose_ports_hold_it()
    {
        var (db, pins) = await StartAsync(maria);
        var (low, high, region) = (Server(HostA, firstPort: 9000), Server(HostA, firstPort: 9100), Guid.NewGuid());
        await db.AddRegion(region, HostA, port: 9100);
        await pins.PinWaitingAsync(low, 9000, 65536, Ct);

        Assert.Equal(1, await pins.PinWaitingAsync(high, 9100, 65536, Ct));

        Assert.Empty(await pins.WaitingAsync(low.Id, Ct));
        Assert.Equal([region], await pins.WaitingAsync(high.Id, Ct));
    }

    [Fact]
    public async Task A_region_already_pinned_to_another_server_is_not_made_waiting()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, b, region) = (Server(HostA), Server(HostB), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, b);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");

        Assert.Equal(0, await pins.PinWaitingAsync(a, a.FirstPort, 65536, Ct));

        Assert.Equal(b.Id.ToString("N"), await db.Text($"SELECT server_id FROM `money`.`region_pins` WHERE region_id = '{region}'"));
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`region_pins` WHERE waiting = 1"));
    }

    [Fact]
    public async Task A_waiting_region_raises_no_move_alert_and_a_wrong_server_listing_it_is_a_move()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, b, region) = (Server(HostA), Server(HostB), Guid.NewGuid());
        await db.AddRegion(region, HostA);
        await pins.PinWaitingAsync(a, a.FirstPort, 65536, Ct);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostB}' WHERE uuid = '{region}'");

        Assert.Empty(await Covered(pins, b));
        Assert.Equal([new RegionMove(region, a.Id, b.Id)], await pins.MovesAsync([a, b], Ct));
    }

    [Fact]
    public async Task Pins_to_a_server_no_longer_paired_show_as_moves()
    {
        var (db, pins) = await StartAsync(maria);
        var (gone, a, region) = (Server(HostB), Server(HostA), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, gone);
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");

        Assert.Equal([new RegionMove(region, gone.Id, a.Id)], await pins.MovesAsync([a], Ct));
        Assert.Empty(await Covered(pins, a));
    }

    [Fact]
    public async Task Same_machine_repairing_keeps_pins()
    {
        var (db, pins) = await StartAsync(maria);
        var (old, region) = (Server(HostA, "machine-1"), Guid.NewGuid());
        var replacement = Server(HostA, "machine-1");
        await db.AddRegion(region, HostA);
        await Covered(pins, old);
        await db.Sql($"INSERT INTO `money`.`limit_overrides` (kind, target, daily_spend, reason, staff, at) VALUES ('key', 'server:{old.Id:N}', 500, 'bigger', 'Owner', UTC_TIMESTAMP(6))");

        await pins.CarryOverAsync(old, replacement, Ct);

        Assert.Equal(new HashSet<Guid> { region }, (await Covered(pins, replacement)).ToHashSet());
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`limit_overrides` WHERE kind = 'key' AND target = 'server:{replacement.Id:N}' AND daily_spend = 500"));
        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`limit_overrides` WHERE target = 'server:{old.Id:N}'"));
        Assert.Empty(await pins.MovesAsync([replacement], Ct));
    }

    [Fact]
    public async Task Same_machine_repairing_carries_who_it_placed_and_handed()
    {
        // The people the server had verified, and the handoffs it sent, are the new key's: its regions keep their money.
        var (db, pins) = await StartAsync(maria);
        var (old, region, session) = (Server(HostA, "machine-1"), Guid.NewGuid(), Guid.NewGuid());
        var replacement = Server(HostA, "machine-1");
        await db.AddRegion(region, HostA);
        await Covered(pins, old);
        await db.Sql($"""
            INSERT INTO `money`.`session_locations` (session_id, agent_id, region_id, key_id, state, last_start_id, updated) VALUES
              ('{session}', '{Guid.NewGuid()}', '{region}', 'server:{old.Id:N}', 'Verified', 1, UTC_TIMESTAMP(6));
            INSERT INTO `money`.`handoffs` (id, session_id, from_key, from_region, to_region, kind, created) VALUES
              ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 'server:{old.Id:N}', '{region}', '{Guid.NewGuid()}', 'Teleport', UTC_TIMESTAMP(6));
            """);

        await pins.CarryOverAsync(old, replacement, Ct);

        Assert.Equal($"server:{replacement.Id:N}", await db.Text($"SELECT key_id FROM `money`.`session_locations` WHERE session_id = '{session}'"));
        Assert.Equal($"server:{replacement.Id:N}", await db.Text("SELECT from_key FROM `money`.`handoffs`"));
    }

    [Fact]
    public async Task Different_machine_at_same_address_and_port_gets_no_pins()
    {
        var (db, pins) = await StartAsync(maria);
        var (old, region) = (Server(HostA, "machine-1"), Guid.NewGuid());
        var other = Server(HostA, "machine-2");
        await db.AddRegion(region, HostA);
        await Covered(pins, old);

        await pins.CarryOverAsync(old, other, Ct);

        Assert.Empty(await Covered(pins, other));
        Assert.Equal([new RegionMove(region, old.Id, other.Id)], await pins.MovesAsync([other], Ct));
    }

    [Fact]
    public async Task Without_machine_ids_nothing_carries_over()
    {
        var (db, pins) = await StartAsync(maria);
        var (old, region) = (Server(HostA), Guid.NewGuid());
        var replacement = Server(HostA);
        await db.AddRegion(region, HostA);
        await Covered(pins, old);

        await pins.CarryOverAsync(old, replacement, Ct);

        Assert.Empty(await Covered(pins, replacement));
    }

    [Fact]
    public async Task Same_machine_repairing_keeps_confirmed_pins_confirmed()
    {
        var (db, pins) = await StartAsync(maria);
        var (old, region) = (Server(HostA, "machine-1"), Guid.NewGuid());
        var replacement = Server(HostA, "machine-1");
        await db.AddRegion(region, HostA);
        await Covered(pins, old);

        await pins.CarryOverAsync(old, replacement, Ct);
        Assert.Equal(0, await pins.PinWaitingAsync(replacement, replacement.FirstPort, 65536, Ct));

        Assert.Equal(new HashSet<Guid> { region }, (await Covered(pins, replacement)).ToHashSet());
    }

    [Fact]
    public async Task Database_down_is_unavailable()
    {
        var db = await maria.FreshAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var pins = new RegionPins(ct => Core.Database.GridDb.OpenMoneyAsync(closed, 2, ct), new RegionHosts(db.OpenRobust), TimeProvider.System);
        await db.AddRegion(Guid.NewGuid(), HostA);

        await Assert.ThrowsAsync<MoneyUnavailableException>(() => Covered(pins, Server(HostA)));
        await Assert.ThrowsAsync<MoneyUnavailableException>(() => pins.PinWaitingAsync(Server(HostA), 9000, 65536, Ct));
        await Assert.ThrowsAsync<MoneyUnavailableException>(() => pins.ConfirmAllAsync(Server(HostA), Ct));
        await Assert.ThrowsAsync<MoneyUnavailableException>(() => pins.WaitingAsync(Guid.NewGuid(), Ct));
    }
}
