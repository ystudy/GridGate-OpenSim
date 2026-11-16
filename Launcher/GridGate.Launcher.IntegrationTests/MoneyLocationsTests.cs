using GridGate.Launcher.Core.Money;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The money service's own record of where each session is (money design §8.4, review finding L-1). Programs p1 (R1, R5)
/// and p2 (R2) are local programs whose modules have checked in; p4 (R4) has never called (stock OpenSim, so R4 isn't
/// verified); the paired Region Server (R3) has checked in. Resident U and visitor V are logged in.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLocationsTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid U = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid V = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid SessionU = Guid.Parse("5000000a-0000-0000-0000-000000000005");
    private static readonly Guid SecureU = Guid.Parse("6000000a-0000-0000-0000-000000000006");
    private static readonly Guid SessionV = Guid.Parse("5000000c-0000-0000-0000-000000000005");
    private static readonly Guid SecureV = Guid.Parse("6000000c-0000-0000-0000-000000000006");
    private static readonly Guid R1 = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid R2 = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid R3 = Guid.Parse("e0000000-0000-0000-0000-000000000003"); // Neon Pier, on a paired server
    private static readonly Guid R4 = Guid.Parse("e0000000-0000-0000-0000-000000000004");
    private static readonly Guid R5 = Guid.Parse("e0000000-0000-0000-0000-000000000005");
    private static readonly Guid Elsewhere = Guid.Parse("e0000000-0000-0000-0000-0000000000ff"); // a region on another grid

    private static readonly RegionKeyGrant P1 = new("p1", new HashSet<Guid> { R1, R5 });
    private static readonly RegionKeyGrant P2 = new("p2", new HashSet<Guid> { R2 });
    private static readonly RegionKeyGrant P4 = new("p4", new HashSet<Guid> { R4 });
    private static readonly RegionKeyGrant Paired = new("server:" + Guid.Parse("d0000000-0000-0000-0000-00000000000d").ToString("N"),
        new HashSet<Guid> { R3 }, "192.168.1.20", 9000, 65536);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MoneyTestDatabase _db = null!;
    private FakeTimeProvider _clock = null!;
    private MoneyLocations _locations = null!;
    private RegionVerification _verification = null!;

    private async Task StartAsync()
    {
        _db = await maria.FreshAsync();
        await _db.EnsureSchemaAsync();
        await _db.AddPresence(U, R1, SessionU, SecureU);
        await _db.AddPresence(V, R1, SessionV, SecureV);
        _clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var pins = new RegionPins(_db.OpenMoney, new RegionHosts(_db.OpenRobust), _clock);
        _verification = new RegionVerification(_db.OpenMoney, _db.OpenRobust, pins, _clock);
        await ModulesCheckIn();
        _locations = new MoneyLocations(_db.OpenMoney, _db.OpenRobust, _verification, new PresenceCheck(_db.OpenRobust), _clock);
    }

    /// <summary>The health calls that make p1, p2 and the paired server verified; again after the clock moves past <see cref="RegionVerification.HeardWithin"/>.</summary>
    private async Task ModulesCheckIn()
    {
        await _verification.HeardAsync(P1.KeyId, RegionVerification.HandoffProtocol, [R1, R5], Ct);
        await _verification.HeardAsync(P2.KeyId, RegionVerification.HandoffProtocol, [R2], Ct);
        await _verification.HeardAsync(Paired.KeyId, RegionVerification.HandoffProtocol, [R3], Ct);
    }

    /// <summary>
    /// Waits for <paramref name="task"/>, moving the fake clock on while it is pending, so an arrival held for its handoff
    /// gets its retries and, without one, its answer after <see cref="MoneyLocations.ArrivalWait"/>.
    /// </summary>
    private async Task<T> Settle<T>(Task<T> task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "the arrival never answered");
            await Task.WhenAny(task, Task.Delay(20, Ct));
            if (!task.IsCompleted) _clock.Advance(MoneyLocations.ArrivalRetry);
        }
        return await task;
    }

    private Task<ArrivalResult> Arrive(RegionKeyGrant grant, Guid region, Guid? session = null, Guid? agent = null) =>
        Settle(_locations.ArrivedAsync(grant, session ?? SessionU, agent ?? U, region, Ct));

    private Task<PlaceOutcome> Handoff(RegionKeyGrant grant, Guid from, Guid to, HandoffKind kind = HandoffKind.Teleport, Guid? id = null, Guid? session = null) =>
        _locations.HandoffAsync(grant, id ?? Guid.NewGuid(), session ?? SessionU, from, to, kind, Ct);

    /// <summary>U logs in at R1 and arrives there: verified at R1.</summary>
    private async Task LoggedInAtR1()
    {
        await _db.AddSessionStart(SessionU, U, R1, "login");
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P1, R1)).Outcome);
    }

    private Task<SessionLocation?> Where(Guid? session = null) => _locations.WhereAsync(session ?? SessionU, Ct);

    private static void AssertAt(SessionLocation? location, Guid region, string key, LocationState state)
    {
        Assert.NotNull(location);
        Assert.Equal((region, key, state), (location.RegionId, location.KeyId, location.State));
    }

    private Task<long> VerifiedRows() => _db.Scalar("SELECT COUNT(*) FROM `money`.`session_locations` WHERE state = 'Verified'");

    private Task<long> OpenHandoffs() =>
        _db.Scalar($"SELECT COUNT(*) FROM `money`.`handoffs` WHERE session_id = '{SessionU}' AND kind IN ('Teleport', 'Crossing')");

    [Fact]
    public async Task Where_an_agent_is_is_their_newest_logged_in_session()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Null(await _locations.WhereAgentAsync(V, Ct)); // logged in, but never placed

        // A second viewer logs U in again later, at R4, which can't verify anyone.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var session2 = Guid.Parse("5000000a-0000-0000-0000-0000000000b2");
        await _db.AddPresence(U, R4, session2, Guid.NewGuid());
        await _db.AddSessionStart(session2, U, R4, "login");
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P4, R4, session2)).Outcome);

        var newest = await _locations.WhereAgentAsync(U, Ct);
        Assert.Equal(session2, newest?.SessionId);
        AssertAt(newest, R4, "p4", LocationState.Unverified);

        // The newer session logs out: its row stays a while, but only a logged-in session says where U is.
        await _db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{session2}'");
        var older = await _locations.WhereAgentAsync(U, Ct);
        Assert.Equal(SessionU, older?.SessionId);
        AssertAt(older, R1, "p1", LocationState.Verified);

        await _db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionU}'");
        Assert.Null(await _locations.WhereAgentAsync(U, Ct));
    }

    [Fact]
    public async Task Login_start_then_arrival_is_verified()
    {
        await StartAsync();
        var start = await _db.AddSessionStart(SessionU, U, R1, "login");

        var result = await Arrive(P1, R1);

        Assert.Equal(PlaceOutcome.Ok, result.Outcome);
        Assert.Null(result.VerifiedElsewhere);
        var at = await Where();
        AssertAt(at, R1, "p1", LocationState.Verified);
        Assert.Equal(U, at!.AgentId);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, at.Updated, TimeSpan.FromSeconds(1));
        Assert.Equal(start, await _db.Scalar($"SELECT last_start_id FROM `money`.`session_locations` WHERE session_id = '{SessionU}'"));
    }

    [Fact]
    public async Task Neon_Pier_claim_pauses_money_but_never_verifies_the_claimer()
    {
        await StartAsync();
        await LoggedInAtR1();
        var before = await Where();

        // Neon Pier saw U next door and claims they arrived, to charge them. Two keys disagree (Ruling 12): U is paused.
        var result = await Arrive(Paired, R3);

        Assert.Equal(PlaceOutcome.Conflict, result.Outcome);
        Assert.Equal(before, result.VerifiedElsewhere);
        AssertAt(await Where(), R3, Paired.KeyId, LocationState.Unverified);
        Assert.Equal(0, await VerifiedRows());
    }

    [Fact]
    public async Task Withheld_handoff_cannot_keep_charging_someone_who_left()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R3, "login");
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(Paired, R3)).Outcome); // U really was in Neon Pier

        // U teleports to R2; Neon Pier keeps its handoff back so U would stay its verified, chargeable avatar.
        var result = await Arrive(P2, R2);

        Assert.Equal(PlaceOutcome.Conflict, result.Outcome);
        AssertAt(result.VerifiedElsewhere, R3, Paired.KeyId, LocationState.Verified);
        AssertAt(await Where(), R2, "p2", LocationState.Unverified);
        Assert.Equal(0, await VerifiedRows());
    }

    [Fact]
    public async Task Claim_from_the_same_key_keeps_the_place()
    {
        await StartAsync();
        await LoggedInAtR1();
        var before = await Where();

        // R5 runs in the same program as R1: one key can't disagree with itself, so there is nothing to pause for.
        var result = await Arrive(P1, R5);

        Assert.Equal(PlaceOutcome.Refused, result.Outcome);
        Assert.Equal(before, result.VerifiedElsewhere);
        Assert.Equal(before, await Where());
    }

    [Fact]
    public async Task Claim_from_an_unverified_region_keeps_the_place()
    {
        await StartAsync();
        await LoggedInAtR1();
        var before = await Where();

        // R4 isn't verified: it must not be able to knock anyone out.
        var result = await Arrive(P4, R4);

        Assert.Equal(PlaceOutcome.Refused, result.Outcome);
        Assert.Equal(before, result.VerifiedElsewhere);
        Assert.Equal(before, await Where());
    }

    [Fact]
    public async Task Teleport_handoff_then_arrival_is_verified()
    {
        await StartAsync();
        await LoggedInAtR1();

        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2));
        var result = await Arrive(P2, R2);

        Assert.Equal(PlaceOutcome.Ok, result.Outcome);
        AssertAt(await Where(), R2, "p2", LocationState.Verified);
        Assert.Equal(0, await OpenHandoffs()); // consumed
    }

    [Fact]
    public async Task Crossing_handoff_then_arrival_is_verified()
    {
        await StartAsync();
        await LoggedInAtR1();

        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R5, HandoffKind.Crossing));

        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P1, R5)).Outcome);
        AssertAt(await Where(), R5, "p1", LocationState.Verified);
    }

    [Fact]
    public async Task Arrival_before_handoff_is_held_and_then_verified()
    {
        await StartAsync();
        await LoggedInAtR1();

        var arrival = _locations.ArrivedAsync(P2, SessionU, U, R2, Ct);
        await Task.Delay(300, Ct);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100, Ct);
        Assert.False(arrival.IsCompleted, "the arrival should be held for its handoff");

        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2));

        Assert.Equal(PlaceOutcome.Ok, (await Settle(arrival)).Outcome);
        AssertAt(await Where(), R2, "p2", LocationState.Verified);
    }

    [Fact]
    public async Task Arrival_with_no_handoff_after_10_seconds_is_refused()
    {
        await StartAsync();
        await LoggedInAtR1();

        var arrival = _locations.ArrivedAsync(P1, SessionU, U, R5, Ct); // the same key: refused, not paused (Ruling 12)
        for (var i = 0; i < 19; i++) // 9.5 s
        {
            await Task.Delay(30, Ct);
            _clock.Advance(MoneyLocations.ArrivalRetry);
        }
        await Task.Delay(100, Ct);
        Assert.False(arrival.IsCompleted, "the arrival is held for 10 seconds");

        var result = await Settle(arrival);

        Assert.Equal(PlaceOutcome.Refused, result.Outcome);
        AssertAt(result.VerifiedElsewhere, R1, "p1", LocationState.Verified);
        AssertAt(await Where(), R1, "p1", LocationState.Verified);
    }

    [Fact]
    public async Task Expired_handoff_does_not_count()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R5));

        _clock.Advance(MoneyLocations.HandoffLife + TimeSpan.FromSeconds(1));

        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P1, R5)).Outcome);
        AssertAt(await Where(), R5, "p1", LocationState.Unverified);
    }

    [Fact]
    public async Task A_handoff_moves_the_session_and_it_is_not_payable_until_the_arrival()
    {
        await StartAsync();
        await LoggedInAtR1();

        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2));

        // Handed on: no longer at R1, and not verified anywhere until R2 reports the arrival.
        var handed = await Where();
        AssertAt(handed, R2, "p1", LocationState.Handed);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, handed!.Updated, TimeSpan.FromSeconds(1));
        Assert.Equal(0, await VerifiedRows());
        // The region left can't hand them on again.
        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(P1, R1, R5));

        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P2, R2)).Outcome);
        AssertAt(await Where(), R2, "p2", LocationState.Verified);
    }

    [Fact]
    public async Task Handed_to_a_region_that_never_reports_stays_not_payable()
    {
        await StartAsync();
        await LoggedInAtR1();

        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R4)); // R4 runs stock OpenSim: no arrival will come
        _clock.Advance(TimeSpan.FromHours(1));

        AssertAt(await Where(), R4, "p1", LocationState.Handed);
        Assert.Equal(0, await VerifiedRows());
    }

    [Fact]
    public async Task Coming_back_from_a_region_that_never_reports_is_not_a_conflict()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R4));

        // From R4 (no handoff: stock OpenSim) on to R2, another key's verified region. Nobody else has them verified, so
        // the arrival is answered at once, unverified, and nobody is blamed.
        var result = await _locations.ArrivedAsync(P2, SessionU, U, R2, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(PlaceOutcome.PlacedUnverified, result.Outcome);
        Assert.Null(result.VerifiedElsewhere);
        AssertAt(await Where(), R2, "p2", LocationState.Unverified);
    }

    [Fact]
    public async Task Only_a_verified_session_holds_an_unmatched_arrival()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R4, "login");
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P4, R4)).Outcome);

        // No handoff can be on its way for an unverified session, nor for one never placed: both are answered at once, with
        // the clock standing still.
        var unverified = await _locations.ArrivedAsync(P2, SessionU, U, R2, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var unplaced = await _locations.ArrivedAsync(P2, SessionV, V, R2, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(PlaceOutcome.PlacedUnverified, unverified.Outcome);
        Assert.Equal(PlaceOutcome.PlacedUnverified, unplaced.Outcome);
    }

    [Fact]
    public async Task An_arrival_elsewhere_while_handed_is_unverified()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2));

        var result = await _locations.ArrivedAsync(Paired, SessionU, U, R3, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(PlaceOutcome.PlacedUnverified, result.Outcome);
        AssertAt(await Where(), R3, Paired.KeyId, LocationState.Unverified);
        // The handoff to R2 can't verify them any more: they are no longer handed there.
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P2, R2)).Outcome);
        Assert.Equal(0, await VerifiedRows());
    }

    [Fact]
    public async Task A_destination_claim_without_a_handoff_cannot_verify()
    {
        await StartAsync();
        await LoggedInAtR1();

        // A teleport or crossing to R2 that failed sends no handoff; R2 claiming the arrival anyway can't verify U.
        var result = await Arrive(P2, R2);

        Assert.Equal(PlaceOutcome.Conflict, result.Outcome);
        Assert.Equal(0, await VerifiedRows());
    }

    [Fact]
    public async Task Cancel_restores_a_session_that_did_not_leave_the_grid()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid));
        Assert.Equal(LocationState.LeftGrid, (await Where())!.State);

        await _locations.CancelHandoffAsync(P2, SessionU, R1, Ct); // not p2's region: ignored
        Assert.Equal(LocationState.LeftGrid, (await Where())!.State);
        await _locations.CancelHandoffAsync(P1, SessionU, R5, Ct); // not where it was handed from: ignored
        Assert.Equal(LocationState.LeftGrid, (await Where())!.State);

        await _locations.CancelHandoffAsync(P1, SessionU, R1, Ct);

        AssertAt(await Where(), R1, "p1", LocationState.Verified);
    }

    [Fact]
    public async Task Cancel_changes_nothing_once_the_session_moved_on()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid));
        await _db.AddSessionStart(SessionU, U, R2, "gatekeeper");
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P2, R2)).Outcome); // back home through the Gatekeeper

        await _locations.CancelHandoffAsync(P1, SessionU, R1, Ct);

        AssertAt(await Where(), R2, "p2", LocationState.Verified);
    }

    [Fact]
    public async Task Cancel_leaves_a_teleport_handoff_alone()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2));

        await _locations.CancelHandoffAsync(P1, SessionU, R1, Ct);

        Assert.Equal(1, await OpenHandoffs());
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P2, R2)).Outcome);
        AssertAt(await Where(), R2, "p2", LocationState.Verified);
    }

    [Fact]
    public async Task Cancel_after_the_handoff_life_changes_nothing()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid));
        _clock.Advance(MoneyLocations.HandoffLife + TimeSpan.FromSeconds(1));

        await _locations.CancelHandoffAsync(P1, SessionU, R1, Ct);

        Assert.Equal(LocationState.LeftGrid, (await Where())!.State);
    }

    [Fact]
    public async Task Gatekeeper_start_replaces_a_handed_place()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R4)); // into stock OpenSim, then on to another grid and back

        await _db.AddSessionStart(SessionU, U, R2, "gatekeeper");

        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P2, R2)).Outcome);
        AssertAt(await Where(), R2, "p2", LocationState.Verified);
    }

    [Fact]
    public async Task Handoff_only_from_the_key_that_placed_the_session()
    {
        await StartAsync();
        await LoggedInAtR1();
        // R1 is now p2's too (say the owner moved it to another program), but U was placed there under p1.
        var p2WithR1 = new RegionKeyGrant("p2", new HashSet<Guid> { R1, R2 });

        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(p2WithR1, R1, R5));
        AssertAt(await Where(), R1, "p1", LocationState.Verified);
    }

    [Fact]
    public async Task Handoff_for_someone_elsewhere_is_not_held()
    {
        await StartAsync();
        await LoggedInAtR1();

        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(P2, R2, R3)); // U isn't in R2
        Assert.Equal(PlaceOutcome.NotCovered, await Handoff(P2, R1, R2)); // R1 isn't p2's
        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(P1, R5, R2)); // p1's region, but U is in R1
        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(P1, R1, R2, session: SessionV)); // V was never placed

        Assert.Equal(0, await _db.Scalar("SELECT COUNT(*) FROM `money`.`handoffs`"));
        Assert.Null(await Where(SessionV)); // a handoff never creates a location
        AssertAt(await Where(), R1, "p1", LocationState.Verified);
    }

    [Fact]
    public async Task Arrival_from_unverified_region_is_unverified_until_next_login()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R4, "login");
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P4, R4)).Outcome); // R4 runs stock OpenSim
        AssertAt(await Where(), R4, "p4", LocationState.Unverified);

        // Stock OpenSim sends no handoff; nobody can vouch for where U came from.
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P1, R1)).Outcome);
        AssertAt(await Where(), R1, "p1", LocationState.Unverified);
        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(P1, R1, R2));
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P2, R2)).Outcome);

        // The next login is a new session, verified again.
        var session2 = Guid.NewGuid();
        await _db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionU}'");
        await _db.AddPresence(U, R1, session2, Guid.NewGuid());
        await _db.AddSessionStart(session2, U, R1, "login");
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P1, R1, session2)).Outcome);
        AssertAt(await Where(session2), R1, "p1", LocationState.Verified);
    }

    [Fact]
    public async Task Handoff_into_unverified_region_leaves_them_unverified()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R4));

        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P4, R4)).Outcome);
        AssertAt(await Where(), R4, "p4", LocationState.Unverified);

        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(P4, R4, R1));
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P1, R1)).Outcome);
        AssertAt(await Where(), R1, "p1", LocationState.Unverified);
    }

    [Fact]
    public async Task Gatekeeper_start_in_local_region_is_verified()
    {
        await StartAsync();
        var start = await _db.AddSessionStart(SessionV, V, R1, "gatekeeper");

        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P1, R1, SessionV, V)).Outcome);

        AssertAt(await Where(SessionV), R1, "p1", LocationState.Verified);
        Assert.Equal(start, await _db.Scalar($"SELECT last_start_id FROM `money`.`session_locations` WHERE session_id = '{SessionV}'"));
    }

    [Fact]
    public async Task Gatekeeper_start_in_paired_server_region_is_not_counted()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionV, V, R3, "gatekeeper");

        // The owner's rule (§8.4): only the Grid Asset Server's own regions count a Gatekeeper start.
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(Paired, R3, SessionV, V)).Outcome);

        AssertAt(await Where(SessionV), R3, Paired.KeyId, LocationState.Unverified);
        Assert.Equal(0, await _db.Scalar($"SELECT last_start_id FROM `money`.`session_locations` WHERE session_id = '{SessionV}'"));
    }

    [Fact]
    public async Task Gatekeeper_replay_while_here_is_refused()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionV, V, R1, "gatekeeper");
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P1, R1, SessionV, V)).Outcome);

        // V's own grid asks our Gatekeeper to send them to R5 (same program) while they are still in R1. Through another
        // key's region the same replay pauses V instead (Ruling 12, as the Neon Pier test shows); either way no start
        // moves a verified session.
        await _db.AddSessionStart(SessionV, V, R5, "gatekeeper");
        var result = await Arrive(P1, R5, SessionV, V);

        Assert.Equal(PlaceOutcome.Refused, result.Outcome);
        AssertAt(await Where(SessionV), R1, "p1", LocationState.Verified);
    }

    [Fact]
    public async Task Resident_home_after_left_grid_via_local_region_is_verified()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid));
        var away = await Where();
        Assert.Equal(LocationState.LeftGrid, away!.State);
        Assert.Null(away.RegionId);

        // The old login start can't place them again; a new Gatekeeper start into a local region can.
        await _db.AddSessionStart(SessionU, U, R2, "gatekeeper");
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P2, R2)).Outcome);

        AssertAt(await Where(), R2, "p2", LocationState.Verified);
    }

    [Fact]
    public async Task Arrival_for_logged_out_session_is_no_session()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R1, "login");
        await _db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionU}'");

        Assert.Equal(PlaceOutcome.NoSession, (await Arrive(P1, R1)).Outcome);
        Assert.Equal(PlaceOutcome.NoSession, (await Arrive(P1, R1, SessionV, U)).Outcome); // V's session, not U's

        Assert.Equal(0, await _db.Scalar("SELECT COUNT(*) FROM `money`.`session_locations`"));
    }

    [Fact]
    public async Task Arrival_in_a_region_the_key_doesnt_cover_is_not_covered()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R1, "login");

        Assert.Equal(PlaceOutcome.NotCovered, (await Arrive(P2, R1)).Outcome);

        Assert.Null(await Where());
    }

    [Fact]
    public async Task Missing_session_starts_table_is_unavailable()
    {
        await StartAsync();
        await _db.Sql("DROP TABLE `robust`.`gridgate_session_starts`");

        Assert.Equal(PlaceOutcome.Unavailable, (await Arrive(P1, R1)).Outcome);

        Assert.Equal(0, await _db.Scalar("SELECT COUNT(*) FROM `money`.`session_locations`"));
    }

    [Fact]
    public async Task Same_handoff_id_twice_is_harmless()
    {
        await StartAsync();
        await LoggedInAtR1();
        var id = Guid.NewGuid();

        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2, id: id));
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2, id: id));
        Assert.Equal(1, await _db.Scalar("SELECT COUNT(*) FROM `money`.`handoffs`"));

        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P2, R2)).Outcome);
        // A retry that comes after the arrival used the handoff.
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2, id: id));

        AssertAt(await Where(), R2, "p2", LocationState.Verified);
        Assert.Equal(0, await OpenHandoffs());
        // Another key can't reuse the id.
        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(P2, R2, R1, id: id));
    }

    [Fact]
    public async Task Repeated_arrival_in_the_same_region_is_ok()
    {
        await StartAsync();
        await LoggedInAtR1();
        var before = await Where();
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P1, R1)).Outcome);

        Assert.Equal(before, await Where());
    }

    [Fact]
    public async Task Two_racing_arrivals_for_one_session_place_it_once()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2));

        // The root event reported twice at once: one uses the handoff, the other finds the session already there.
        var results = await Settle(Task.WhenAll(
            _locations.ArrivedAsync(P2, SessionU, U, R2, Ct),
            _locations.ArrivedAsync(P2, SessionU, U, R2, Ct)));

        Assert.All(results, r => Assert.Equal(PlaceOutcome.Ok, r.Outcome));
        AssertAt(await Where(), R2, "p2", LocationState.Verified);
        Assert.Equal(1, await _db.Scalar($"SELECT COUNT(*) FROM `money`.`handoffs` WHERE session_id = '{SessionU}' AND kind = 'Used'"));
    }

    [Fact]
    public async Task Early_false_claim_before_the_login_arrival_pauses_money()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R1, "login");

        // Neon Pier claims U before U's viewer reaches R1: nothing vouches for it, so it can only place U unverified.
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(Paired, R3)).Outcome);
        AssertAt(await Where(), R3, Paired.KeyId, LocationState.Unverified);

        // A login start places only a session not yet placed (Ruling 10), so money is paused until the next login, and
        // nobody is verified anywhere.
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P1, R1)).Outcome);
        AssertAt(await Where(), R1, "p1", LocationState.Unverified);
    }

    [Fact]
    public async Task Withheld_login_arrival_cannot_later_verify_a_moved_avatar()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R3, "login"); // U logs in to Neon Pier, which never reports the arrival

        // U teleports on to R2. Neon Pier's handoff isn't held (it never placed U), so R2 can only place U unverified.
        Assert.Equal(PlaceOutcome.NotHeld, await Handoff(Paired, R3, R2));
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P2, R2)).Outcome);
        AssertAt(await Where(), R2, "p2", LocationState.Unverified);

        // Now Neon Pier claims U with the login start it held back.
        var claim = await Arrive(Paired, R3);

        Assert.NotEqual(PlaceOutcome.Ok, claim.Outcome);
        Assert.NotEqual(LocationState.Verified, (await Where())!.State);
        Assert.Equal(0, await _db.Scalar("SELECT COUNT(*) FROM `money`.`session_locations` WHERE state = 'Verified'"));
    }

    [Fact]
    public async Task Late_retry_of_a_cancelled_handoff_changes_nothing()
    {
        await StartAsync();
        await LoggedInAtR1();
        var id = Guid.NewGuid();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid, id: id));
        await _locations.CancelHandoffAsync(P1, SessionU, R1, Ct); // the teleport to the other grid failed

        // The module's late retry of the same send.
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid, id: id));

        AssertAt(await Where(), R1, "p1", LocationState.Verified);
    }

    [Fact]
    public async Task Early_false_claim_does_not_block_coming_home()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid));

        // While U is away, Neon Pier claims them.
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(Paired, R3)).Outcome);

        // U comes home through the Gatekeeper into a local region: the new start still places them (Ruling 8).
        await _db.AddSessionStart(SessionU, U, R2, "gatekeeper");
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P2, R2)).Outcome);
        AssertAt(await Where(), R2, "p2", LocationState.Verified);
    }

    [Fact]
    public async Task A_consumed_start_cannot_upgrade_twice()
    {
        await StartAsync();
        await LoggedInAtR1(); // uses the login start
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R4));
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P4, R4)).Outcome); // into stock OpenSim

        // Back in R1, the login start that was already used can't verify U again.
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P1, R1)).Outcome);
        AssertAt(await Where(), R1, "p1", LocationState.Unverified);
    }

    [Fact]
    public async Task Cleanup_drops_logged_out_sessions()
    {
        await StartAsync();
        await LoggedInAtR1();
        await _db.AddSessionStart(SessionV, V, R1, "gatekeeper");
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P1, R1, SessionV, V)).Outcome);
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2));
        _clock.Advance(MoneyLocations.KeepAbsentFor + TimeSpan.FromMinutes(1));
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, R2, session: SessionV)); // still open
        await _db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionU}'"); // U logs out

        await _locations.CleanupAsync(Ct);

        Assert.Null(await Where());
        AssertAt(await Where(SessionV), R2, "p1", LocationState.Handed); // still logged in, however old its row
        Assert.Equal(0, await _db.Scalar($"SELECT COUNT(*) FROM `money`.`handoffs` WHERE session_id = '{SessionU}'"));
        Assert.Equal(1, await _db.Scalar($"SELECT COUNT(*) FROM `money`.`handoffs` WHERE session_id = '{SessionV}'"));
    }

    [Fact]
    public async Task Cleanup_keeps_rows_of_absent_sessions_for_two_days()
    {
        await StartAsync();
        await LoggedInAtR1();
        // Teleporting to another grid logs the session out of Presence; coming back logs it in again with the same id.
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid));
        await _db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionU}'");

        _clock.Advance(MoneyLocations.KeepAbsentFor - TimeSpan.FromMinutes(1));
        await _locations.CleanupAsync(Ct);

        Assert.Equal(LocationState.LeftGrid, (await Where())?.State);
    }

    [Fact]
    public async Task Cleanup_drops_absent_sessions_after_two_days()
    {
        await StartAsync();
        await LoggedInAtR1();
        Assert.Equal(PlaceOutcome.Ok, await Handoff(P1, R1, Elsewhere, HandoffKind.OtherGrid));
        await _db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionU}'");

        _clock.Advance(MoneyLocations.KeepAbsentFor + TimeSpan.FromMinutes(1));
        await _locations.CleanupAsync(Ct);

        Assert.Null(await Where());
    }

    [Fact]
    public async Task Leaving_the_grid_and_returning_does_not_rearm_a_used_login_start()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R3, "login"); // U logs in at Neon Pier, which learns the session id
        Assert.Equal(PlaceOutcome.Ok, (await Arrive(Paired, R3)).Outcome);
        Assert.Equal(PlaceOutcome.Ok, await Handoff(Paired, R3, Elsewhere, HandoffKind.OtherGrid));
        await _db.Sql($"DELETE FROM `robust`.`Presence` WHERE SessionID = '{SessionU}'"); // HG logout while away

        _clock.Advance(TimeSpan.FromHours(3));
        await _locations.CleanupAsync(Ct);
        await ModulesCheckIn();

        // U comes home through the Gatekeeper, same session id, into the local region R2.
        await _db.AddPresence(U, R2, SessionU, SecureU);
        await _db.AddSessionStart(SessionU, U, R2, "gatekeeper");
        // Neon Pier claims U again with the login start it already used.
        Assert.NotEqual(PlaceOutcome.Ok, (await Arrive(Paired, R3)).Outcome);

        Assert.Equal(PlaceOutcome.Ok, (await Arrive(P2, R2)).Outcome);
        AssertAt(await Where(), R2, "p2", LocationState.Verified);
    }

    [Fact]
    public async Task Starts_older_than_a_day_are_ignored()
    {
        await StartAsync();
        await _db.AddSessionStart(SessionU, U, R1, "login");
        await _db.AddSessionStart(SessionV, V, R1, "gatekeeper");
        await _db.Sql("UPDATE `robust`.`gridgate_session_starts` SET `At` = NOW() - INTERVAL 2 DAY");

        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P1, R1)).Outcome);
        Assert.Equal(PlaceOutcome.PlacedUnverified, (await Arrive(P1, R1, SessionV, V)).Outcome);
    }
}
