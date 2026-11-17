using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

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
        return (db, new RegionPins(db.OpenMoney, db.OpenRobust, new RegionHosts(db.OpenRobust), TimeProvider.System));
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
    public async Task Removing_a_server_drops_its_pins()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, b, region) = (Server(HostA), Server(HostB), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, b);

        await pins.RemoveServerAsync(b.Id, Ct);

        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`region_pins`"));
        // Listed at A now: nobody holds a pin on it any more, so A is the first to see it.
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");
        Assert.Contains(region, await Covered(pins, a));
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

    private static Task<long> Floor(MoneyTestDatabase db, Guid region) =>
        db.Scalar($"SELECT start_floor FROM `money`.`region_pins` WHERE region_id = '{region}'");

    private static Task<long> PinnedAt(MoneyTestDatabase db, Guid region, DateTimeOffset at) =>
        db.Scalar($"SELECT COUNT(*) FROM `money`.`region_pins` WHERE region_id = '{region}' AND pinned = '{at.UtcDateTime:yyyy-MM-dd HH:mm:ss.ffffff}'");

    private static async Task<(MoneyTestDatabase Db, RegionPins Pins, FakeTimeProvider Clock)> StartWithClockAsync(MoneyMariaDb maria)
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return (db, new RegionPins(db.OpenMoney, db.OpenRobust, new RegionHosts(db.OpenRobust), clock), clock);
    }

    [Fact]
    public async Task First_sight_keeps_the_newest_start_as_the_floor()
    {
        // Logins from before the pin aren't the server's to use: the pin keeps the newest start Robust had written then.
        var (db, pins, clock) = await StartWithClockAsync(maria);
        var (a, region) = (Server(HostA), Guid.NewGuid());
        await db.AddSessionStart(Guid.NewGuid(), Guid.NewGuid(), region, "login");
        var newest = await db.AddSessionStart(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "login");
        await db.AddRegion(region, HostA);

        await Covered(pins, a);

        Assert.Equal(newest, await Floor(db, region));
        Assert.Equal(1, await PinnedAt(db, region, clock.GetUtcNow()));
        // Later starts and later looks leave the pin as it was.
        await db.AddSessionStart(Guid.NewGuid(), Guid.NewGuid(), region, "login");
        clock.Advance(TimeSpan.FromMinutes(1));
        await Covered(pins, a);
        Assert.Equal(newest, await Floor(db, region));
        Assert.Equal(1, await PinnedAt(db, region, clock.GetUtcNow() - TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task First_sight_with_no_starts_has_floor_0()
    {
        var (db, pins) = await StartAsync(maria);
        var (a, region) = (Server(HostA), Guid.NewGuid());
        await db.AddRegion(region, HostA);

        await Covered(pins, a);

        Assert.Equal(0, await Floor(db, region));
    }

    [Fact]
    public async Task Approving_a_move_gives_a_new_pin_time_and_floor()
    {
        var (db, pins, clock) = await StartWithClockAsync(maria);
        var (a, b, region) = (Server(HostA), Server(HostB), Guid.NewGuid());
        await db.AddRegion(region, HostB);
        await Covered(pins, b);
        var newest = await db.AddSessionStart(Guid.NewGuid(), Guid.NewGuid(), region, "login");
        clock.Advance(TimeSpan.FromHours(1));
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{HostA}' WHERE uuid = '{region}'");

        await pins.ApproveMoveAsync(region, a.Id, Ct);

        Assert.Equal(newest, await Floor(db, region));
        Assert.Equal(1, await PinnedAt(db, region, clock.GetUtcNow()));
    }

    [Fact]
    public async Task Same_machine_repairing_keeps_the_pin_time_and_floor()
    {
        // The same machine already covered the region: what it saw before stays its own.
        var (db, pins, clock) = await StartWithClockAsync(maria);
        var (old, region) = (Server(HostA, "machine-1"), Guid.NewGuid());
        var replacement = Server(HostA, "machine-1");
        var floor = await db.AddSessionStart(Guid.NewGuid(), Guid.NewGuid(), region, "login");
        await db.AddRegion(region, HostA);
        await Covered(pins, old);
        var pinned = clock.GetUtcNow();
        await db.AddSessionStart(Guid.NewGuid(), Guid.NewGuid(), region, "login");
        clock.Advance(TimeSpan.FromHours(1));

        await pins.CarryOverAsync(old, replacement, Ct);
        await Covered(pins, replacement);

        Assert.Equal(floor, await Floor(db, region));
        Assert.Equal(1, await PinnedAt(db, region, pinned));
    }

    [Fact]
    public async Task Robust_down_is_unavailable_and_pins_nothing()
    {
        // Without the newest start a pin can't be made safely, at first sight or at an approved move.
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var (a, b, region, moving) = (Server(HostA), Server(HostB), Guid.NewGuid(), Guid.NewGuid());
        await db.AddRegion(moving, HostB);
        await Covered(new RegionPins(db.OpenMoney, db.OpenRobust, new RegionHosts(db.OpenRobust), TimeProvider.System), b);
        await db.AddRegion(region, HostA);
        var pins = new RegionPins(db.OpenMoney, ct => Core.Database.GridDb.OpenRobustAsync(closed, 2, ct), new RegionHosts(db.OpenRobust), TimeProvider.System);

        await Assert.ThrowsAsync<MoneyUnavailableException>(() => Covered(pins, a));
        await Assert.ThrowsAsync<MoneyUnavailableException>(() => pins.ApproveMoveAsync(moving, a.Id, Ct));

        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`region_pins` WHERE region_id = '{region}'"));
        Assert.Equal(b.Id.ToString("N"), await db.Text($"SELECT server_id FROM `money`.`region_pins` WHERE region_id = '{moving}'"));
    }

    [Fact]
    public async Task Database_down_is_unavailable()
    {
        var db = await maria.FreshAsync();
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var pins = new RegionPins(ct => Core.Database.GridDb.OpenMoneyAsync(closed, 2, ct), db.OpenRobust, new RegionHosts(db.OpenRobust), TimeProvider.System);
        await db.AddRegion(Guid.NewGuid(), HostA);

        await Assert.ThrowsAsync<MoneyUnavailableException>(() => Covered(pins, Server(HostA)));
        await Assert.ThrowsAsync<MoneyUnavailableException>(() => pins.RemoveServerAsync(Guid.NewGuid(), Ct));
    }
}
