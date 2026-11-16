using System.Diagnostics;
using System.Net;
using System.Text.Json;
using GridGate.Launcher.Core.Logs;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The owner's alerts: arrivals a key's region claimed without being handed the avatar, daily limits
/// reached, and regions moving between servers. One row per kind and group per 24 hours, counted up; a row's level only
/// rises. Raising an alert never changes the answer that raised it.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyAlertsTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private static readonly Guid R1 = Guid.Parse("e1000000-0000-0000-0000-0000000000e1");
    private static readonly Guid R2 = Guid.Parse("e2000000-0000-0000-0000-0000000000e2");
    private static readonly Guid Ann = Guid.Parse("a1000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bob = Guid.Parse("b1000000-0000-0000-0000-0000000000b1");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
    private MoneyServiceRig? _rig;
    private AuditLog? _audit;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null) await _rig.DisposeAsync();
        _audit?.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(MoneyTestDatabase Db, MoneyAlerts Alerts)> StartAsync()
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        Directory.CreateDirectory(_dir);
        _audit = new AuditLog(Path.Combine(_dir, "audit.log"), _clock);
        return (db, new MoneyAlerts(db.OpenMoney, _clock, audit: _audit));
    }

    private static async Task<MoneyAlert> Single(MoneyAlerts alerts) => Assert.Single(await alerts.OpenAsync(Ct));

    private List<JsonElement> AuditLines()
    {
        var path = Path.Combine(_dir, "audit.log");
        if (!File.Exists(path)) return [];
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(s).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
    }

    [Fact]
    public async Task One_unhanded_arrival_is_a_notice()
    {
        var (_, alerts) = await StartAsync();

        // Verified elsewhere under the same key: it can't disagree with itself, so this is a lost handoff at worst.
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, "p1", Ct);

        var a = await Single(alerts);
        Assert.Equal((AlertKind.UnhandedArrival, AlertLevel.Notice, "p1", R1, Ann, 1), (a.Kind, a.Level, a.KeyId, a.RegionId, a.AgentId, a.Count));
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, a.FirstAt);
        Assert.Equal(DateTimeKind.Utc, a.LastAt.Kind);
    }

    [Fact]
    public async Task Third_from_one_key_is_a_warning()
    {
        var (_, alerts) = await StartAsync();

        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await alerts.UnhandedArrivalAsync("p1", R1, Bob, null, Ct);
        await alerts.UnhandedArrivalAsync("p2", R1, Bob, null, Ct); // another key's: counted for p2 only
        var two = (await alerts.OpenAsync(Ct)).Single(a => a.KeyId == "p1");
        Assert.Equal((AlertLevel.Notice, 2, Bob), (two.Level, two.Count, two.AgentId)); // the row keeps the last person

        _clock.Advance(TimeSpan.FromHours(1));
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct);

        var three = (await alerts.OpenAsync(Ct)).Single(a => a.KeyId == "p1");
        Assert.Equal((AlertLevel.Warning, 3, Ann), (three.Level, three.Count, three.AgentId));
        Assert.True(three.Repeated); // a warning from the key's own repeated claims
        Assert.Null(three.OtherKeyId);
        Assert.Equal(AlertLevel.Notice, (await alerts.OpenAsync(Ct)).Single(a => a.KeyId == "p2").Level);
    }

    [Fact]
    public async Task Third_from_one_key_across_its_regions_is_a_warning()
    {
        var (_, alerts) = await StartAsync();

        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct);
        await alerts.UnhandedArrivalAsync("p1", R1, Bob, null, Ct);
        await alerts.UnhandedArrivalAsync("p1", R2, Ann, null, Ct);

        var open = await alerts.OpenAsync(Ct);
        Assert.Equal(2, open.Count); // one row per key and region
        Assert.Equal((AlertLevel.Warning, 1), open.Where(a => a.RegionId == R2).Select(a => (a.Level, a.Count)).Single());
    }

    [Fact]
    public async Task Arrivals_more_than_a_day_apart_start_a_new_count()
    {
        var (_, alerts) = await StartAsync();

        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct);
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct);
        _clock.Advance(MoneyAlerts.Window + TimeSpan.FromMinutes(1));
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct);

        var open = await alerts.OpenAsync(Ct);
        Assert.Equal(2, open.Count);
        Assert.All(open, a => Assert.Equal(AlertLevel.Notice, a.Level));
        Assert.Equal([1, 2], open.Select(a => a.Count).Order());
    }

    [Fact]
    public async Task Unhanded_arrival_for_someone_verified_under_another_key_is_a_warning_at_once()
    {
        var (_, alerts) = await StartAsync();

        await alerts.UnhandedArrivalAsync("p2", R2, Ann, "p1", Ct);

        var a = await Single(alerts);
        Assert.Equal((AlertKind.UnhandedArrival, AlertLevel.Warning, "p2", 1), (a.Kind, a.Level, a.KeyId, a.Count));
        // Both servers are named: either may be the one that is wrong. It isn't a count of the claimer's own claims.
        Assert.Equal("p1", a.OtherKeyId);
        Assert.False(a.Repeated);
    }

    [Fact]
    public async Task A_conflict_then_repeated_claims_keeps_both_servers_and_counts_as_repeated()
    {
        var (_, alerts) = await StartAsync();

        await alerts.UnhandedArrivalAsync("p2", R2, Ann, "p1", Ct);
        await alerts.UnhandedArrivalAsync("p2", R2, Bob, null, Ct);
        Assert.False((await Single(alerts)).Repeated);
        await alerts.UnhandedArrivalAsync("p2", R2, Bob, "p2", Ct); // the same key elsewhere: no other server

        var a = await Single(alerts);
        Assert.Equal((AlertLevel.Warning, 3, "p1", true), (a.Level, a.Count, a.OtherKeyId, a.Repeated));
    }

    [Fact]
    public async Task A_warning_never_falls_back_to_a_notice()
    {
        var (_, alerts) = await StartAsync();

        await alerts.UnhandedArrivalAsync("p2", R2, Ann, "p1", Ct);
        await alerts.UnhandedArrivalAsync("p2", R2, Bob, null, Ct);

        var a = await Single(alerts);
        Assert.Equal((AlertLevel.Warning, 2, Bob), (a.Level, a.Count, a.AgentId));
    }

    [Fact]
    public async Task Key_limit_reached_is_a_warning()
    {
        var (_, alerts) = await StartAsync();

        await alerts.KeyLimitReachedAsync("p1", Ct);
        await alerts.KeyLimitReachedAsync("p1", Ct);

        var a = await Single(alerts);
        Assert.Equal((AlertKind.KeyLimitReached, AlertLevel.Warning, "p1", 2), (a.Kind, a.Level, a.KeyId, a.Count));
        Assert.Null(a.RegionId);
    }

    [Fact]
    public async Task Region_moved_is_a_notice()
    {
        var (_, alerts) = await StartAsync();

        await alerts.RegionMovedAsync(R1, "server:aaaa", Ct);
        await alerts.RegionMovedAsync(R1, "server:bbbb", Ct);

        var a = await Single(alerts);
        Assert.Equal((AlertKind.RegionMoved, AlertLevel.Notice, "server:bbbb", R1, 2), (a.Kind, a.Level, a.KeyId, a.RegionId, a.Count));
    }

    [Fact]
    public async Task Person_limit_alert_appears_on_the_third_hit()
    {
        var (_, alerts) = await StartAsync();

        await alerts.PersonLimitHitAsync(Ann, "p1", Ct);
        await alerts.PersonLimitHitAsync(Ann, "p1", Ct);
        await alerts.PersonLimitHitAsync(Bob, "p1", Ct);
        Assert.Empty(await alerts.OpenAsync(Ct));

        await alerts.PersonLimitHitAsync(Ann, "p2", Ct);

        var a = await Single(alerts);
        Assert.Equal((AlertKind.PersonLimitRepeated, AlertLevel.Notice, Ann, "p2", 3), (a.Kind, a.Level, a.AgentId, a.KeyId, a.Count));
    }

    [Fact]
    public async Task Cleared_alert_is_not_open()
    {
        var (db, alerts) = await StartAsync();
        await alerts.KeyLimitReachedAsync("p1", Ct);
        var id = (await Single(alerts)).Id;

        await alerts.ClearAsync(id, "Test Owner", Ct);

        Assert.Empty(await alerts.OpenAsync(Ct));
        Assert.Equal("Test Owner", await db.Text($"SELECT cleared_by FROM `money`.`alerts` WHERE id = {id}"));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`alerts` WHERE id = {id} AND cleared IS NOT NULL"));
        // The next one starts a new row and a new count.
        await alerts.KeyLimitReachedAsync("p1", Ct);
        var next = await Single(alerts);
        Assert.NotEqual(id, next.Id);
        Assert.Equal(1, next.Count);
    }

    [Fact]
    public async Task Audit_gets_a_line_when_an_alert_opens_or_becomes_a_warning()
    {
        var (_, alerts) = await StartAsync();

        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct); // opens as a notice
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct); // counted: no line
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct); // becomes a warning
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct); // counted: no line
        await alerts.PersonLimitHitAsync(Bob, "p1", Ct);
        await alerts.PersonLimitHitAsync(Bob, "p1", Ct);
        await alerts.PersonLimitHitAsync(Bob, "p1", Ct); // appears on the third

        var lines = AuditLines();
        Assert.Equal(["notice", "warning", "notice"], lines.Select(l => l.GetProperty("result").GetString()));
        Assert.All(lines, l => Assert.Equal("money-alert", l.GetProperty("action").GetString()));
        Assert.All(lines, l => Assert.Equal("GridGate", l.GetProperty("actor").GetString()));
        Assert.Contains("UnhandedArrival", lines[0].GetProperty("target").GetString());
        Assert.Contains(R1.ToString(), lines[0].GetProperty("target").GetString());
        Assert.Contains("PersonLimitRepeated", lines[2].GetProperty("target").GetString());
        Assert.Contains(Bob.ToString(), lines[2].GetProperty("target").GetString());
    }

    private static PairedServer Server(string host, string machine) =>
        new(Guid.NewGuid(), host, new string('a', 64), DateTimeOffset.UtcNow, 9000, machine);

    private static string KeyOf(PairedServer s) => "server:" + s.Id.ToString("N");

    /// <summary>R1 first seen at <paramref name="a"/>'s address (pinned there), then Robust lists it at <paramref name="b"/>'s.</summary>
    private static async Task MoveR1(MoneyTestDatabase db, RegionPins pins, PairedServer a, PairedServer b)
    {
        await db.AddRegion(R1, a.Host);
        Assert.Contains(R1, await pins.CoveredAsync(a, 9000, 65536, new HashSet<Guid>(), Ct, raiseMoves: true));
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{b.Host}' WHERE uuid = '{R1}'");
    }

    [Fact]
    public async Task A_region_listed_at_another_server_raises_a_move()
    {
        var (db, alerts) = await StartAsync();
        var pins = new RegionPins(db.OpenMoney, new RegionHosts(db.OpenRobust), TimeProvider.System, alerts);
        var (a, b) = (Server("192.168.1.20", "m-a"), Server("192.168.1.30", "m-b"));
        await MoveR1(db, pins, a, b);
        Assert.Empty(await alerts.OpenAsync(Ct)); // first sight is a pin, not a move

        Assert.Empty(await pins.CoveredAsync(b, 9000, 65536, new HashSet<Guid>(), Ct, raiseMoves: true));

        var moved = await Single(alerts);
        Assert.Equal((AlertKind.RegionMoved, AlertLevel.Notice, R1, KeyOf(b), 1), (moved.Kind, moved.Level, moved.RegionId, moved.KeyId, moved.Count));
    }

    [Fact]
    public async Task Many_requests_listing_a_moved_region_give_one_row_and_one_audit_line()
    {
        var (db, alerts) = await StartAsync();
        var pins = new RegionPins(db.OpenMoney, new RegionHosts(db.OpenRobust), TimeProvider.System, alerts);
        var (a, b, c) = (Server("192.168.1.20", "m-a"), Server("192.168.1.30", "m-b"), Server("192.168.1.40", "m-c"));
        await MoveR1(db, pins, a, b);

        for (var i = 0; i < 20; i++)
            await pins.CoveredAsync(b, 9000, 65536, new HashSet<Guid>(), Ct, raiseMoves: true);
        // A restart forgets what it raised; the open row still says this move was seen.
        var restarted = new MoneyAlerts(db.OpenMoney, _clock, audit: _audit);
        await new RegionPins(db.OpenMoney, new RegionHosts(db.OpenRobust), TimeProvider.System, restarted)
            .CoveredAsync(b, 9000, 65536, new HashSet<Guid>(), Ct, raiseMoves: true);

        var moved = await Single(alerts);
        Assert.Equal((KeyOf(b), 1), (moved.KeyId, moved.Count));
        Assert.Single(AuditLines());

        // Listed at a third server: another move seen, on the same open row.
        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{c.Host}' WHERE uuid = '{R1}'");
        await pins.CoveredAsync(c, 9000, 65536, new HashSet<Guid>(), Ct, raiseMoves: true);
        await pins.CoveredAsync(c, 9000, 65536, new HashSet<Guid>(), Ct, raiseMoves: true);
        moved = await Single(alerts);
        Assert.Equal((KeyOf(c), 2), (moved.KeyId, moved.Count));
        Assert.Single(AuditLines());
    }

    [Fact]
    public async Task The_website_status_read_raises_no_alert()
    {
        var (db, alerts) = await StartAsync();
        var pins = new RegionPins(db.OpenMoney, new RegionHosts(db.OpenRobust), TimeProvider.System, alerts);
        var (a, b) = (Server("192.168.1.20", "m-a"), Server("192.168.1.30", "m-b"));
        await MoveR1(db, pins, a, b);
        var verification = new RegionVerification(db.OpenMoney, db.OpenRobust, pins, _clock);

        var states = await verification.AllAsync(db.Settings with { Money = db.Settings.Money with { PairedServers = [a, b] } }, Ct);

        Assert.Contains(states, s => s.RegionId == R1);
        Assert.Empty(await alerts.OpenAsync(Ct));
        Assert.Empty(await pins.CoveredAsync(b, 9000, 65536, new HashSet<Guid>(), Ct)); // nor does a read that doesn't ask to
        Assert.Empty(await alerts.OpenAsync(Ct));
    }

    [Fact]
    public async Task Third_within_a_rolling_day_is_a_warning_even_across_rows()
    {
        var (_, alerts) = await StartAsync();

        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct);
        _clock.Advance(TimeSpan.FromHours(23));
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct); // the row's last one, 23 h after its first
        _clock.Advance(TimeSpan.FromHours(2));
        await alerts.UnhandedArrivalAsync("p1", R1, Ann, null, Ct); // past the first row's window: a new row

        var open = await alerts.OpenAsync(Ct);
        Assert.Equal(2, open.Count);
        Assert.Equal(AlertLevel.Warning, open.Single(a => a.Count == 1).Level); // two within the day before it
    }

    [Fact]
    public async Task A_cancelled_raise_never_throws()
    {
        var (_, alerts) = await StartAsync();

        await alerts.KeyLimitReachedAsync("p1", new CancellationToken(canceled: true));
        await alerts.RegionMovedAsync(R1, "server:aaaa", new CancellationToken(canceled: true));

        Assert.Empty(await alerts.OpenAsync(Ct));
        // Not written, so not remembered either: the next one is.
        await alerts.RegionMovedAsync(R1, "server:aaaa", Ct);
        Assert.Single(await alerts.OpenAsync(Ct));
    }

    [Fact]
    public async Task Warnings_read_isnt_crowded_out_by_notices()
    {
        var (db, alerts) = await StartAsync();
        await alerts.KeyLimitReachedAsync("p1", Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await db.Sql("INSERT INTO `money`.`alerts` (kind, level, key_id, region_id, count, first_at, last_at) " +
            $"SELECT 'RegionMoved', 'Notice', 'server:x', UUID(), 1, '{_clock.GetUtcNow():yyyy-MM-dd HH:mm:ss}', '{_clock.GetUtcNow():yyyy-MM-dd HH:mm:ss}' FROM `money`.seq_1_to_600");

        Assert.DoesNotContain(await alerts.OpenAsync(Ct), a => a.Level == AlertLevel.Warning); // 500 newer notices
        var warning = Assert.Single(await alerts.WarningsAsync(Ct));
        Assert.Equal(AlertKind.KeyLimitReached, warning.Kind);
    }

    [Fact]
    public async Task The_open_arrivals_cap_raises_no_alert()
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);
        // 25 people, each verified at There under p2, each claimed by p1's Here with no handoff. The location clock stands
        // still, so 20 are held and 5 are refused by the cap before anything is looked up.
        var people = Enumerable.Range(0, 25).Select(_ => (Agent: Guid.NewGuid(), Session: Guid.NewGuid())).ToList();
        foreach (var (agent, session) in people)
        {
            await rig.Db.AddPresence(agent, There, session, Guid.NewGuid());
            await rig.Db.AddSessionStart(session, agent, There, "login");
            Assert.Equal("ok", CodeOf(await rig.Arrived(Key2, session, agent, There)));
        }
        var calls = people.Select(p => rig.Post("arrived", new { id = Guid.NewGuid(), sessionId = p.Session, agentId = p.Agent, regionId = Here })).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (calls.Count(c => c.IsCompleted) < 5)
        {
            Assert.True(DateTime.UtcNow < deadline, "the arrivals past 20 were never refused");
            await Task.WhenAny(Task.WhenAll(calls), Task.Delay(20, Ct));
        }
        foreach (var refused in calls.Where(c => c.IsCompleted)) Assert.Equal("refused", CodeOf(await refused));

        Assert.Empty(await rig.Alerts.OpenAsync(Ct));

        // The held ones answer once their wait runs out: each a claim against another key's place, so each is an alert.
        while (calls.Any(c => !c.IsCompleted))
        {
            Assert.True(DateTime.UtcNow < deadline.AddSeconds(30), "the held arrivals never answered");
            await Task.WhenAny(Task.WhenAll(calls), Task.Delay(20, Ct));
            rig.LocationClock.Advance(MoneyLocations.ArrivalRetry);
        }
        Assert.Equal(20, Assert.Single(await rig.Alerts.OpenAsync(Ct)).Count);
    }

    [Fact]
    public async Task Payout_over_the_owners_daily_limit_raises_no_person_alert()
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);
        Assert.Equal(MoneyOutcome.Ok, (await rig.Ledger.SetPayoutsAllowedAsync(A, true, Ct)).Outcome);
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { DailyPayoutLimit = 1 } };

        for (var i = 0; i < 3; i++)
            Assert.Equal("over_daily_limit", CodeOf(await rig.Post("payout",
                new { txnId = Guid.NewGuid(), ownerId = A, to = new { agentId = B, home = "" }, amount = 5, regionId = Here, objectId = Guid.NewGuid(), objectName = "slot" })));

        Assert.Empty(await rig.Alerts.OpenAsync(Ct));
        Assert.Equal(0, await rig.Db.Scalar("SELECT COUNT(*) FROM `money`.`alerts`"));
    }

    [Fact]
    public async Task Conflicting_arrival_through_the_service_is_a_warning()
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);

        // A is verified at Here under p1; p2's verified There claims A arrived with no handoff.
        Assert.Equal("unverified_avatar", CodeOf(await rig.Arrived(Key2, SessionA, A, There)));

        var a = Assert.Single(await rig.Alerts.OpenAsync(Ct));
        Assert.Equal((AlertKind.UnhandedArrival, AlertLevel.Warning, "p2", There, A), (a.Kind, a.Level, a.KeyId, a.RegionId, a.AgentId));
    }

    [Fact]
    public async Task Refused_arrival_does_not_slow_the_key()
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria, place: false);
        await rig.Health(Key);
        Assert.Equal("ok", await rig.Place(SessionA, Here));

        // p2 hasn't checked in, so its There can't knock A out of Here: refused, each time.
        for (var i = 0; i < 3; i++)
            Assert.Equal("refused", CodeOf(await rig.Arrived(Key2, SessionA, A, There)));

        Assert.Equal(0, rig.Failures.Tracked);
        var watch = Stopwatch.StartNew();
        var next = await rig.Post("health", new { protocol = RegionVerification.HandoffProtocol, regions = new[] { There } }, Key2);
        Assert.Equal(HttpStatusCode.OK, next.Status);
        Assert.True(watch.Elapsed < MoneyAuthFailures.SlowDown, $"the next call took {watch.Elapsed}");
        var a = Assert.Single(await rig.Alerts.OpenAsync(Ct));
        Assert.Equal((AlertLevel.Warning, 3), (a.Level, a.Count)); // A is verified under p1, not p2
    }

    [Fact]
    public async Task Payments_over_a_limit_raise_alerts()
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria);
        object Pay() => new { txnId = Guid.NewGuid(), type = "pay_avatar", from = WhoA(), to = new { agentId = B, home = "" }, amount = 10, regionId = Here };

        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { DailyKeySpendLimit = 5 } };
        Assert.Equal("over_key_limit", CodeOf(await rig.Post("transfer", Pay())));
        var key = Assert.Single(await rig.Alerts.OpenAsync(Ct));
        Assert.Equal((AlertKind.KeyLimitReached, AlertLevel.Warning, "p1"), (key.Kind, key.Level, key.KeyId));

        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { DailyKeySpendLimit = 100_000, DailySpendLimit = 5 } };
        for (var i = 0; i < 3; i++)
            Assert.Equal("over_daily_limit", CodeOf(await rig.Post("transfer", Pay())));
        var person = (await rig.Alerts.OpenAsync(Ct)).Single(a => a.Kind == AlertKind.PersonLimitRepeated);
        Assert.Equal((AlertLevel.Notice, A, "p1", 3), (person.Level, person.AgentId, person.KeyId, person.Count));
    }

    [Fact]
    public async Task An_alert_that_cant_be_written_changes_no_answer()
    {
        var rig = _rig = await MoneyServiceRig.StartAsync(maria, alertsDown: true);

        Assert.Equal("unverified_avatar", CodeOf(await rig.Arrived(Key2, SessionA, A, There)));
        rig.Settings = rig.Settings with { Money = rig.Settings.Money with { DailySpendLimit = 5 } };
        Assert.Equal("over_daily_limit", CodeOf(await rig.Post("transfer",
            new { txnId = Guid.NewGuid(), type = "pay_avatar", from = WhoB(), to = new { agentId = A, home = "" }, amount = 10, regionId = There }, Key2)));

        Assert.Contains(rig.Logs, l => l.Contains("money alert", StringComparison.OrdinalIgnoreCase) && l.Contains("MySqlException"));
        await Assert.ThrowsAsync<MoneyUnavailableException>(() => rig.Alerts.OpenAsync(Ct));
    }
}
