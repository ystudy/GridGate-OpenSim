using System.Data;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// What the money service knows about where a session is: verified (a start or a handoff from a verified location put it
/// in a verified region), unverified (nobody could vouch for it, so money is off until the next login), gone to another
/// grid, or handed (the region it was verified in handed it to another region, which hasn't reported it yet: money is off
/// until that region does, or until the next login).
/// </summary>
public enum LocationState { Verified, Unverified, LeftGrid, Handed }

/// <summary>How an avatar leaves a region: a teleport (home, landmark, map, offer), a border crossing, or a teleport to another grid.</summary>
public enum HandoffKind { Teleport, Crossing, OtherGrid }

/// <summary>
/// The answer to a handoff or an arrival. <see cref="PlacedUnverified"/>: the session is placed, but money is off for it.
/// <see cref="NotHeld"/>: the key's regions don't hold the session. <see cref="Refused"/>: an arrival nothing vouches for,
/// while the session is verified elsewhere under the same key, or from a region that isn't verified; nothing changes.
/// <see cref="Conflict"/>: an arrival nothing vouches for, from a verified region, while the session is verified under
/// another key. Two keys disagree about where the avatar is, so it is placed unverified at the arriving region and nobody
/// can charge it until a new start verifies it.
/// </summary>
public enum PlaceOutcome { Ok, PlacedUnverified, NoSession, NotCovered, NotHeld, Refused, Unavailable, Conflict }

/// <summary>
/// Where a session is, as the money service recorded it. <paramref name="KeyId"/> is the key that placed it (for a handed
/// session, the key that handed it on); both are null once it left the grid.
/// </summary>
public sealed record SessionLocation(Guid SessionId, Guid AgentId, Guid? RegionId, string? KeyId, LocationState State, DateTime Updated);

/// <summary>
/// An arrival's answer. <paramref name="VerifiedElsewhere"/> is the verified place the session had before, set when the
/// answer is <see cref="PlaceOutcome.Refused"/> or <see cref="PlaceOutcome.Conflict"/>, so the caller can count it against
/// the key and raise the alert.
/// </summary>
public sealed record ArrivalResult(PlaceOutcome Outcome, SessionLocation? VerifiedElsewhere);

/// <summary>
/// The money service's own record of where each session is, kept in the money database so a launcher restart keeps it.
/// Robust's <c>Presence</c> only proves a session is logged in; its RegionID, which any region can rewrite, is never read.
/// A location moves only by a start that Robust wrote (<c>gridgate_session_starts</c>) or by a handoff from the key whose
/// region holds the session. Each placement is one money-database transaction holding the session's row lock, so racing
/// calls for one session decide one at a time. Starts and handoffs keep the address Robust listed their region at when
/// they happened; a paired Region Server's key counts only those that went to its own address.
/// </summary>
public sealed class MoneyLocations(Func<CancellationToken, Task<MySqlConnection>> openMoney,
    Func<CancellationToken, Task<MySqlConnection>> openRobust, RegionVerification verification, PresenceCheck presence, TimeProvider time)
{
    /// <summary>A handoff counts for this long; an arrival with no match, for a verified session, is held this long before it is answered.</summary>
    public static readonly TimeSpan HandoffLife = TimeSpan.FromMinutes(2), ArrivalWait = TimeSpan.FromSeconds(10);

    /// <summary>How often a held arrival looks again for its handoff or start.</summary>
    public static readonly TimeSpan ArrivalRetry = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long cleanup keeps the location of a session that isn't in Presence. Teleporting to another grid logs a session
    /// out of Presence, and coming back logs it in again with the same id.
    /// </summary>
    public static readonly TimeSpan KeepAbsentFor = TimeSpan.FromDays(2);

    private const int DeadlockRetries = 3;
    private const int ErDeadlock = 1213, ErLockWaitTimeout = 1205;

    /// <summary>The most of one person's sessions <see cref="WhereAgentAsync"/> looks at, newest first.</summary>
    private const int MaxSessionsLooked = 20;

    /// <summary>The state of the row an arrival adds to lock a session the service has never placed; only a placement commits it, with a real state.</summary>
    private const string Placeholder = "Pending";

    /// <summary>
    /// A handoff an arrival used, a newer handoff replaced, or a failed teleport to another grid took back. The row stays
    /// until cleanup, so a late retry of it is still recognised and changes nothing. Only Teleport and Crossing rows are open.
    /// </summary>
    private const string Used = "Used", Replaced = "Replaced", Cancelled = "Cancelled";

    /// <summary>The kinds an arrival can match: a teleport to another grid ends at no region of ours.</summary>
    private const string OpenKinds = "('Teleport', 'Crossing')";

    private const string Columns = "agent_id, region_id, key_id, state, last_start_id, updated";

    private sealed record Row(SessionLocation Location, long LastStartId, bool IsPlaceholder);

    /// <summary>What vouches for an arrival: a handoff (<paramref name="HandoffId"/>) or a start (<paramref name="StartId"/>).</summary>
    private sealed record Match(string? HandoffId, long? StartId);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A key's statement that <paramref name="sessionId"/> has left its region <paramref name="fromRegion"/> for
    /// <paramref name="toRegion"/>. Accepted only while the session is verified at <paramref name="fromRegion"/>, placed by
    /// this key. A teleport or crossing makes the session handed, at <paramref name="toRegion"/>, until that region's
    /// arrival verifies it there; a teleport to another grid makes it "left the grid". Never creates a location.
    /// </summary>
    public async Task<PlaceOutcome> HandoffAsync(RegionKeyGrant grant, Guid handoffId, Guid sessionId, Guid fromRegion, Guid toRegion, HandoffKind kind, CancellationToken ct)
    {
        if (!grant.Regions.Contains(fromRegion)) return PlaceOutcome.NotCovered;
        try
        {
            // Where Robust lists the destination now, as the avatar goes there: a paired server's key counts the handoff only
            // if that is its own address (FindMatchAsync), whatever the region id is registered at later. Read before the
            // transaction, so no row lock waits on Robust.
            var (toHost, toPort) = (default(string), default(long?));
            if (kind != HandoffKind.OtherGrid)
            {
                await using var robust = await openRobust(ct);
                (toHost, toPort) = await RegionHosts.AddressOfAsync(robust, toRegion, ct);
            }
            var known = toHost is not null && toPort is not null;
            return await InTransactionAsync(async (conn, tx) =>
            {
                var row = await LockAsync(conn, tx, sessionId, ct);

                // The module retries a send it got no answer to: the same handoff id answers Ok and changes nothing, even
                // after an arrival used it, a newer handoff replaced it or it was cancelled. An id that another session or
                // key used isn't this key's to repeat.
                await using (var cmd = Command(conn, tx, "SELECT session_id, from_key FROM handoffs WHERE id = @id", ("@id", handoffId.ToString())))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                    if (await r.ReadAsync(ct))
                        return (ToGuid(r.GetValue(0)) == sessionId && r.GetString(1) == grant.KeyId ? PlaceOutcome.Ok : PlaceOutcome.NotHeld, false);

                // Only from the session's verified location, placed by this key; an unverified one stays so until the next login.
                if (row is not { Location.State: LocationState.Verified } || row.Location.RegionId != fromRegion || row.Location.KeyId != grant.KeyId)
                    return (PlaceOutcome.NotHeld, false);

                // A new handoff replaces any open one for the session. Marked, not deleted, so a late retry of the old one is
                // recognised.
                await ExecAsync(conn, tx, $"UPDATE handoffs SET kind = @replaced WHERE session_id = @s AND kind IN {OpenKinds}", ct,
                    ("@replaced", Replaced), ("@s", sessionId.ToString()));
                // One reading of the clock for the handoff and the location it moves: a cancel finds the handoff that made the
                // session "left the grid" by that shared time.
                var now = Now;
                await ExecAsync(conn, tx, """
                    INSERT INTO handoffs (id, session_id, from_key, from_region, to_region, kind, created, to_host, to_port)
                    VALUES (@id, @s, @k, @from, @to, @kind, @t, @toHost, @toPort)
                    """, ct, ("@id", handoffId.ToString()), ("@s", sessionId.ToString()), ("@k", grant.KeyId), ("@from", fromRegion.ToString()),
                    ("@to", toRegion.ToString()), ("@kind", kind.ToString()), ("@t", now),
                    ("@toHost", known ? toHost : null), ("@toPort", known ? toPort : null));

                // A teleport to another grid is "left the grid" at once. Its handoff row is kept so a retry is recognised and
                // a failed teleport can be taken back: arrivals never match that kind.
                if (kind == HandoffKind.OtherGrid)
                    await ExecAsync(conn, tx, "UPDATE session_locations SET region_id = NULL, key_id = NULL, state = @st, updated = @t WHERE session_id = @s", ct,
                        ("@st", LocationState.LeftGrid.ToString()), ("@t", now), ("@s", sessionId.ToString()));
                // A teleport or crossing has completed (the region left lets the avatar go only then): the session is no longer
                // there, and nobody may charge it until the region it went to reports the arrival.
                else
                    await ExecAsync(conn, tx, "UPDATE session_locations SET region_id = @to, state = @st, updated = @t WHERE session_id = @s", ct,
                        ("@to", toRegion.ToString()), ("@st", LocationState.Handed.ToString()), ("@t", now), ("@s", sessionId.ToString()));
                return (PlaceOutcome.Ok, true);
            }, ct);
        }
        catch (Exception e) when (IsDatabaseFailure(e))
        {
            return PlaceOutcome.Unavailable;
        }
    }

    /// <summary>
    /// A failed teleport (<c>OnTeleportFail</c>) out of <paramref name="fromRegion"/>. Only a teleport to another grid moved
    /// the session when it started: while the session is still "left the grid" by this key's handoff from
    /// <paramref name="fromRegion"/>, made within <see cref="HandoffLife"/>, it is verified there again. Anything else changes
    /// nothing. A database failure is <see cref="MoneyUnavailableException"/>.
    /// </summary>
    public async Task CancelHandoffAsync(RegionKeyGrant grant, Guid sessionId, Guid fromRegion, CancellationToken ct)
    {
        if (!grant.Regions.Contains(fromRegion)) return;
        try
        {
            await InTransactionAsync(async (conn, tx) =>
            {
                if (await LockAsync(conn, tx, sessionId, ct) is not { IsPlaceholder: false, Location.State: LocationState.LeftGrid } row) return (false, false);
                // Only the handoff that made the session "left the grid" may be taken back: it was written in the same
                // transaction as the location, with the same time. Any later move would have changed the location since.
                var taken = await ExecAsync(conn, tx, """
                    UPDATE handoffs SET kind = @cancelled
                    WHERE session_id = @s AND from_key = @k AND from_region = @from AND kind = @otherGrid AND created = @left AND created > @since
                    """, ct, ("@cancelled", Cancelled), ("@s", sessionId.ToString()), ("@k", grant.KeyId), ("@from", fromRegion.ToString()),
                    ("@otherGrid", nameof(HandoffKind.OtherGrid)), ("@left", row.Location.Updated), ("@since", Now - HandoffLife));
                if (taken == 0) return (false, false);
                await ExecAsync(conn, tx, "UPDATE session_locations SET region_id = @r, key_id = @k, state = @st, updated = @t WHERE session_id = @s", ct,
                    ("@r", fromRegion.ToString()), ("@k", grant.KeyId), ("@st", LocationState.Verified.ToString()), ("@t", Now), ("@s", sessionId.ToString()));
                return (true, true);
            }, ct);
        }
        catch (Exception e) when (IsDatabaseFailure(e))
        {
            throw e as MoneyUnavailableException ?? new MoneyUnavailableException(e);
        }
    }

    /// <summary>
    /// A key's report that <paramref name="agentId"/> became a root agent in its region <paramref name="regionId"/>. Placed
    /// only when a start or a handoff vouches for it. An unmatched arrival for a verified session is held for
    /// <see cref="ArrivalWait"/>, since its handoff may still be on the way; any other is answered at once.
    /// </summary>
    public async Task<ArrivalResult> ArrivedAsync(RegionKeyGrant grant, Guid sessionId, Guid agentId, Guid regionId, CancellationToken ct)
    {
        try
        {
            // Only Robust's login and the Gatekeeper write Presence, so a row proves the session is real. Nothing is written
            // for a session that isn't logged in.
            switch (await presence.HasSessionAsync(agentId, sessionId, ct))
            {
                case PresenceOutcome.Ok: break;
                case PresenceOutcome.NoSession: return Answer(PlaceOutcome.NoSession);
                default: return Answer(PlaceOutcome.Unavailable);
            }
            if (!grant.Regions.Contains(regionId)) return Answer(PlaceOutcome.NotCovered);

            var deadline = time.GetUtcNow() + ArrivalWait;
            while (true)
            {
                if (await TryPlaceAsync(grant, sessionId, agentId, regionId, deadline, ct) is { } result) return result;
                // An arrival may get there a moment before its handoff. Hold it and look again in a fresh transaction, so no
                // row lock is held while waiting.
                var left = deadline - time.GetUtcNow();
                await Task.Delay(left <= TimeSpan.Zero ? TimeSpan.Zero : left < ArrivalRetry ? left : ArrivalRetry, time, ct);
            }
        }
        catch (Exception e) when (IsDatabaseFailure(e))
        {
            return Answer(PlaceOutcome.Unavailable);
        }
    }

    /// <summary>Where the service has <paramref name="sessionId"/>, or null when it never placed it. A database failure is <see cref="MoneyUnavailableException"/>.</summary>
    public async Task<SessionLocation?> WhereAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await using var conn = await openMoney(ct);
            var row = await ReadAsync(conn, null, sessionId, forUpdate: false, ct);
            return row is null || row.IsPlaceholder ? null : row.Location;
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>
    /// Where <paramref name="agentId"/> is now, for the website: the newest location of theirs whose session is
    /// still logged in (in Robust's Presence), or null when none is. A database failure is <see cref="MoneyUnavailableException"/>.
    /// </summary>
    public async Task<SessionLocation?> WhereAgentAsync(Guid agentId, CancellationToken ct)
    {
        var rows = new List<SessionLocation>();
        try
        {
            await using var conn = await openMoney(ct);
            await using var cmd = Command(conn, null, $"""
                SELECT session_id, region_id, key_id, state, updated FROM session_locations
                WHERE agent_id = @a AND state <> @placeholder ORDER BY updated DESC LIMIT {MaxSessionsLooked}
                """, ("@a", agentId.ToString()), ("@placeholder", Placeholder));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new SessionLocation(ToGuid(r.GetValue(0)), agentId, r.IsDBNull(1) ? null : ToGuid(r.GetValue(1)), r.IsDBNull(2) ? null : r.GetString(2),
                    Enum.Parse<LocationState>(r.GetString(3)), DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc)));
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
        // A logged-out session's row stays for KeepAbsentFor; only Presence says which sessions are still in the world.
        foreach (var row in rows)
            switch (await presence.HasSessionAsync(agentId, row.SessionId, ct))
            {
                case PresenceOutcome.Ok: return row;
                case PresenceOutcome.Unavailable: throw new MoneyUnavailableException(new InvalidOperationException("Robust's Presence can't be read."));
            }
        return null;
    }

    /// <summary>
    /// Drops the locations of sessions that are not in Presence and haven't changed for <see cref="KeepAbsentFor"/>, and
    /// handoffs past <see cref="HandoffLife"/>. A database failure is <see cref="MoneyUnavailableException"/>.
    /// </summary>
    public async Task CleanupAsync(CancellationToken ct)
    {
        const int Batch = 500;
        try
        {
            // A session id comes back: teleporting to another grid logs it out of Presence (HGEntityTransferModule), and
            // the Gatekeeper logs it in again with the same id on the way home. Its row, with the start it already used,
            // must outlive Robust's starts (a day), or that start would count again. So only rows older than
            // KeepAbsentFor go, and only when their session isn't in Presence.
            var cutoff = Now - KeepAbsentFor;
            var sessions = new List<Guid>();
            await using (var conn = await openMoney(ct))
            await using (var cmd = Command(conn, null, "SELECT session_id FROM session_locations WHERE updated < @cutoff", ("@cutoff", cutoff)))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    sessions.Add(ToGuid(r.GetValue(0)));

            var present = new HashSet<Guid>();
            await using (var robust = await openRobust(ct))
                foreach (var chunk in sessions.Chunk(Batch))
                {
                    await using var cmd = InCommand(robust, "SELECT SessionID FROM Presence WHERE SessionID IN", chunk);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        present.Add(ToGuid(r.GetValue(0)));
                }

            await using var money = await openMoney(ct);
            foreach (var chunk in sessions.Where(s => !present.Contains(s)).Chunk(Batch))
            {
                // Rechecked here: a row placed or moved since it was read stays.
                await using var cmd = InCommand(money, "DELETE FROM session_locations WHERE updated < @cutoff AND session_id IN", chunk);
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = Command(money, null, "DELETE FROM handoffs WHERE created < @cutoff", ("@cutoff", Now - HandoffLife)))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>One look for what vouches for an arrival, in one transaction. Null: nothing yet, and the session is verified elsewhere, so hold the arrival.</summary>
    private Task<ArrivalResult?> TryPlaceAsync(RegionKeyGrant grant, Guid sessionId, Guid agentId, Guid regionId, DateTimeOffset deadline, CancellationToken ct) =>
        InTransactionAsync<ArrivalResult?>(async (conn, tx) =>
        {
            // Lock the session's row before deciding, so racing arrivals, and a handoff, decide one at a time. A session never
            // placed gets a placeholder row to lock; rolling back removes it.
            await ExecAsync(conn, tx, """
                INSERT INTO session_locations (session_id, agent_id, state, updated) VALUES (@s, @a, @st, @t)
                ON DUPLICATE KEY UPDATE session_id = session_id
                """, ct, ("@s", sessionId.ToString()), ("@a", agentId.ToString()), ("@st", Placeholder), ("@t", Now));
            var row = (await LockAsync(conn, tx, sessionId, ct))!;
            var here = row.IsPlaceholder ? null : row.Location;

            // Root-agent events can come more than once: verified here already, under this key, is Ok and changes nothing.
            if (here is { State: LocationState.Verified } && here.RegionId == regionId && here.KeyId == grant.KeyId)
                return (Answer(PlaceOutcome.Ok), false);

            if (await FindMatchAsync(conn, tx, grant, sessionId, agentId, regionId, row.IsPlaceholder ? null : row, ct) is { } match)
            {
                // The location becomes that region, verified if the region is verified; otherwise money is off until the next
                // login.
                var verified = await verification.IsVerifiedAsync(grant, regionId, ct);
                if (match.HandoffId is { } used)
                    await ExecAsync(conn, tx, "UPDATE handoffs SET kind = @used WHERE id = @id", ct, ("@used", Used), ("@id", used));
                await PlaceAsync(conn, tx, sessionId, agentId, regionId, grant.KeyId, verified ? LocationState.Verified : LocationState.Unverified,
                    match.StartId ?? row.LastStartId, ct);
                return (Answer(verified ? PlaceOutcome.Ok : PlaceOutcome.PlacedUnverified), true);
            }

            if (here is { State: LocationState.Verified })
            {
                // Only a verified session can have a handoff on its way: hold the arrival a while for it.
                if (time.GetUtcNow() < deadline) return (null, false);

                // A verified region of another key says the avatar is here, while this one has them verified. One of the two
                // keys is wrong, and neither is trusted: the session is placed unverified here, so nobody can charge it until
                // a new start verifies it, and the old place is reported so the caller can raise the alert.
                if (here.KeyId != grant.KeyId && await verification.IsVerifiedAsync(grant, regionId, ct))
                {
                    await PlaceAsync(conn, tx, sessionId, agentId, regionId, grant.KeyId, LocationState.Unverified, row.LastStartId, ct);
                    return (new ArrivalResult(PlaceOutcome.Conflict, here), true);
                }
                // The same key can't disagree with itself, and a region that isn't verified can't move anyone: refused, and
                // the location doesn't change.
                return (new ArrivalResult(PlaceOutcome.Refused, here), false);
            }

            // Nothing vouches for where they came from (unverified, handed somewhere else, or never placed): unverified, money
            // off until the next login.
            await PlaceAsync(conn, tx, sessionId, agentId, regionId, grant.KeyId, LocationState.Unverified, row.LastStartId, ct);
            return (Answer(PlaceOutcome.PlacedUnverified), true);
        }, ct);

    /// <summary>What vouches for an arrival in <paramref name="regionId"/>, in order; <paramref name="row"/> is null when the service never placed the session.</summary>
    private async Task<Match?> FindMatchAsync(MySqlConnection conn, MySqlTransaction tx, RegionKeyGrant grant, Guid sessionId, Guid agentId, Guid regionId,
        Row? row, CancellationToken ct)
    {
        var here = row?.Location;

        // 1. A handoff: the session was handed to this region from its verified place, by a handoff still open and younger
        // than HandoffLife. For a paired server's key it must also have gone to that server: Robust lets any server register
        // any region id, so a server that registers a region later must not use handoffs to the region's real address.
        if (here is { State: LocationState.Handed } && here.RegionId == regionId)
        {
            await using var cmd = Command(conn, tx, $"""
                SELECT id, to_host, to_port FROM handoffs
                WHERE session_id = @s AND to_region = @r AND kind IN {OpenKinds} AND created > @since
                ORDER BY created DESC LIMIT 1
                """, ("@s", sessionId.ToString()), ("@r", regionId.ToString()), ("@since", Now - HandoffLife));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && WentTo(grant, r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2)))
                return new Match(r.GetValue(0).ToString(), null);
        }

        // A start never moves a verified session: a second start through the Gatekeeper is refused while the session is
        // verified here. Only a start newer than last_start_id counts, so each start places once and a used one can't verify
        // the session again.
        var after = row?.LastStartId ?? 0;

        // 2. A login start counts only when the session was never placed. A login region could otherwise hold back its own
        // arrival and use the start once the avatar had moved on and been placed unverified elsewhere. An early claim by
        // another region before the real arrival therefore pauses money until the next login; it can't verify anyone.
        if (row is null && await StartAsync(grant, sessionId, agentId, regionId, "login", after, ct) is { } login)
            return new Match(null, login);

        // 3. A Gatekeeper start counts only in a region of the Grid Asset Server's own (a local program's key, never a
        // paired Region Server's), and only when the session was never placed, left the grid, is unverified or was handed
        // on: those regions report an arrival only when the viewer really connects, so a false claim elsewhere can't keep a
        // resident from coming home.
        if (grant.ServerHost is null && (row is null || here!.State is LocationState.LeftGrid or LocationState.Unverified or LocationState.Handed)
            && await StartAsync(grant, sessionId, agentId, regionId, "gatekeeper", after, ct) is { } gatekeeper)
            return new Match(null, gatekeeper);

        return null;
    }

    /// <summary>
    /// Whether a start or handoff that went to <paramref name="host"/> and UDP port <paramref name="port"/> counts for
    /// <paramref name="grant"/>. A local program's key has no address to check. A paired server's key counts it only when
    /// that address is the server's, by the same comparison that decides which regions it covers
    /// (<see cref="RegionHosts.IsAt"/>); an event with no address counts for no paired key.
    /// </summary>
    private static bool WentTo(RegionKeyGrant grant, string? host, long? port) =>
        grant.ServerHost is not { } server || RegionHosts.IsAt(host, port, server, grant.FromPort, grant.ToPort);

    /// <summary>
    /// The newest start Robust wrote for the session in the region, after <paramref name="after"/> and within the last day
    /// by the database's clock (Robust's own clean-up only runs on logins, so it can't be relied on), that went to
    /// <paramref name="grant"/>'s address (<see cref="WentTo"/>). A missing table throws, and the caller answers Unavailable.
    /// </summary>
    private async Task<long?> StartAsync(RegionKeyGrant grant, Guid sessionId, Guid agentId, Guid regionId, string source, long after, CancellationToken ct)
    {
        await using var conn = await openRobust(ct);
        await using var cmd = Command(conn, null, """
            SELECT Id, ServerIP, ServerPort FROM gridgate_session_starts
            WHERE SessionID = @s AND UserID = @u AND RegionID = @r AND Source = @src AND Id > @after AND At > NOW() - INTERVAL 1 DAY
            ORDER BY Id DESC
            """, ("@s", sessionId.ToString()), ("@u", agentId.ToString()), ("@r", regionId.ToString()), ("@src", source), ("@after", after));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (WentTo(grant, r.IsDBNull(1) ? null : r.GetValue(1).ToString(), r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2))))
                return Convert.ToInt64(r.GetValue(0));
        return null;
    }

    private Task PlaceAsync(MySqlConnection conn, MySqlTransaction tx, Guid sessionId, Guid agentId, Guid regionId, string keyId, LocationState state, long lastStartId, CancellationToken ct) =>
        ExecAsync(conn, tx, """
            UPDATE session_locations SET agent_id = @a, region_id = @r, key_id = @k, state = @st, last_start_id = @start, updated = @t
            WHERE session_id = @s
            """, ct, ("@a", agentId.ToString()), ("@r", regionId.ToString()), ("@k", keyId), ("@st", state.ToString()),
            ("@start", lastStartId), ("@t", Now), ("@s", sessionId.ToString()));

    private static Task<Row?> LockAsync(MySqlConnection conn, MySqlTransaction tx, Guid sessionId, CancellationToken ct) =>
        ReadAsync(conn, tx, sessionId, forUpdate: true, ct);

    private static async Task<Row?> ReadAsync(MySqlConnection conn, MySqlTransaction? tx, Guid sessionId, bool forUpdate, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, $"SELECT {Columns} FROM session_locations WHERE session_id = @s" + (forUpdate ? " FOR UPDATE" : ""),
            ("@s", sessionId.ToString()));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var state = r.GetString(3);
        var placeholder = state == Placeholder;
        var location = new SessionLocation(sessionId, ToGuid(r.GetValue(0)), r.IsDBNull(1) ? null : ToGuid(r.GetValue(1)), r.IsDBNull(2) ? null : r.GetString(2),
            placeholder ? LocationState.Unverified : Enum.Parse<LocationState>(state), DateTime.SpecifyKind(r.GetDateTime(5), DateTimeKind.Utc));
        return new Row(location, r.GetInt64(4), placeholder);
    }

    /// <summary>
    /// One money-database transaction at READ COMMITTED, as the ledger runs them: committed when <paramref name="work"/>
    /// says it wrote, rolled back otherwise. Deadlocks and lock timeouts are retried.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(Func<MySqlConnection, MySqlTransaction, Task<(T Result, bool Write)>> work, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = await openMoney(ct);
                await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                var (result, write) = await work(conn, tx);
                if (write) await tx.CommitAsync(ct);
                else await tx.RollbackAsync(ct);
                return result;
            }
            catch (MySqlException e) when (e.Number is ErDeadlock or ErLockWaitTimeout && attempt < DeadlockRetries)
            {
                await Task.Delay(Random.Shared.Next(5, 30), ct);
            }
        }
    }

    /// <summary>A missing Robust table or any database failure (the verification's own included) is Unavailable, and nothing changes.</summary>
    private static bool IsDatabaseFailure(Exception e) =>
        e is MySqlException or MoneyUnavailableException or TimeoutException or InvalidOperationException { Source: "MySqlConnector" };

    private static ArrivalResult Answer(PlaceOutcome outcome) => new(outcome, null);

    /// <summary>CHAR(36) columns read as <see cref="Guid"/> with MySqlConnector's default settings, as text otherwise.</summary>
    private static Guid ToGuid(object value) => value is Guid g ? g : Guid.Parse(value.ToString()!);

    private static MySqlCommand Command(MySqlConnection conn, MySqlTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }

    /// <summary>Runs <paramref name="sql"/>; the number of rows it matched.</summary>
    private static async Task<int> ExecAsync(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>"<paramref name="sql"/> (@p0, @p1, ...)" over <paramref name="ids"/>.</summary>
    private static MySqlCommand InCommand(MySqlConnection conn, string sql, Guid[] ids)
    {
        var cmd = new MySqlCommand(sql + " (" + string.Join(", ", ids.Select((_, i) => "@p" + i)) + ")", conn);
        for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue("@p" + i, ids[i].ToString());
        return cmd;
    }
}
