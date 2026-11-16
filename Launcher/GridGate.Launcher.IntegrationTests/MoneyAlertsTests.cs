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
/// The owner's alerts (money design §8.8): arrivals a key's region claimed without being handed the avatar, daily limits
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

    [Fact]
    public async Task A_region_listed_at_another_server_raises_a_move()
    {
        var (db, alerts) = await StartAsync();
        var pins = new RegionPins(db.OpenMoney, new RegionHosts(db.OpenRobust), TimeProvider.System, alerts);
        var (a, b) = (new PairedServer(Guid.NewGuid(), "192.168.1.20", new string('a', 64), DateTimeOffset.UtcNow, 9000, "m-a"),
            new PairedServer(Guid.NewGuid(), "192.168.1.30", new string('b', 64), DateTimeOffset.UtcNow, 9000, "m-b"));
        await db.AddRegion(R1, a.Host);
        Assert.Contains(R1, await pins.CoveredAsync(a, 9000, 65536, new HashSet<Guid>(), Ct));
        Assert.Empty(await alerts.OpenAsync(Ct)); // first sight is a pin, not a move

        await db.Sql($"UPDATE `robust`.`regions` SET serverIP = '{b.Host}' WHERE uuid = '{R1}'");
        Assert.Empty(await pins.CoveredAsync(b, 9000, 65536, new HashSet<Guid>(), Ct));

        var moved = await Single(alerts);
        Assert.Equal((AlertKind.RegionMoved, AlertLevel.Notice, R1, "server:" + b.Id.ToString("N")), (moved.Kind, moved.Level, moved.RegionId, moved.KeyId));
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
