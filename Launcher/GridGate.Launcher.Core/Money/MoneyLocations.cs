using System.Data;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// What the money service knows about where a session is (money design §8.4): verified (a start or a handoff from a
/// verified location put it in a verified region), unverified (nobody could vouch for it, so money is off until the next
/// login), or gone to another grid.
/// </summary>
public enum LocationState { Verified, Unverified, LeftGrid }

/// <summary>How an avatar leaves a region (money design §8.4): a teleport (home, landmark, map, offer), a border crossing, or a teleport to another grid.</summary>
public enum HandoffKind { Teleport, Crossing, OtherGrid }

/// <summary>
/// The answer to a handoff or an arrival. <see cref="PlacedUnverified"/>: the session is placed, but money is off for it.
/// <see cref="NotHeld"/>: the key's regions don't hold the session. <see cref="Refused"/>: an arrival nothing vouches for,
/// while the session is verified elsewhere.
/// </summary>
public enum PlaceOutcome { Ok, PlacedUnverified, NoSession, NotCovered, NotHeld, Refused, Unavailable }

/// <summary>Where a session is, as the money service recorded it. <paramref name="KeyId"/> is the key that placed it; both are null once it left the grid.</summary>
public sealed record SessionLocation(Guid SessionId, Guid AgentId, Guid? RegionId, string? KeyId, LocationState State, DateTime Updated);

/// <summary>An arrival's answer; <paramref name="VerifiedElsewhere"/> is set when it is <see cref="PlaceOutcome.Refused"/>.</summary>
public sealed record ArrivalResult(PlaceOutcome Outcome, SessionLocation? VerifiedElsewhere);

/// <summary>
/// The money service's own record of where each session is (money design §8.4, review finding L-1), kept in the money
/// database so a launcher restart keeps it. Robust's <c>Presence</c> only proves a session is logged in; its RegionID,
/// which any region can rewrite, is never read. A location moves only by a start that Robust wrote
/// (<c>gridgate_session_starts</c>) or by a handoff from the key whose region holds the session. Each placement is one
/// money-database transaction holding the session's row lock, so racing calls for one session decide one at a time.
/// </summary>
public sealed class MoneyLocations(Func<CancellationToken, Task<MySqlConnection>> openMoney,
    Func<CancellationToken, Task<MySqlConnection>> openRobust, RegionVerification verification, PresenceCheck presence, TimeProvider time)
{
    /// <summary>A handoff counts for this long (§8.4); an arrival with no match is held this long before it is answered (§8.4).</summary>
    public static readonly TimeSpan HandoffLife = TimeSpan.FromMinutes(2), ArrivalWait = TimeSpan.FromSeconds(10);

    /// <summary>How often a held arrival looks again for its handoff or start.</summary>
    public static readonly TimeSpan ArrivalRetry = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long cleanup keeps the location of a session that isn't in Presence. Teleporting to another grid logs a session
    /// out of Presence, and coming back logs it in again with the same id (Ruling 9).
    /// </summary>
    public static readonly TimeSpan KeepAbsentFor = TimeSpan.FromDays(2);

    private const int DeadlockRetries = 3;
    private const int ErDeadlock = 1213, ErLockWaitTimeout = 1205;

    /// <summary>The state of the row an arrival adds to lock a session the service has never placed; only a placement commits it, with a real state.</summary>
    private const string Placeholder = "Pending";

    /// <summary>
    /// A handoff an arrival used, a newer handoff replaced, or a failed teleport took back. The row stays until cleanup, so a
    /// late retry of it is still recognised and changes nothing: it can't take the place of the newer handoff or reopen a
    /// cancelled destination (Ruling 9). Only Teleport and Crossing rows are open.
    /// </summary>
    private const string Used = "Used", Replaced = "Replaced", Cancelled = "Cancelled";

    /// <summary>The kinds an arrival can match: a teleport to another grid ends at no region of ours.</summary>
    private const string OpenKinds = "('Teleport', 'Crossing')";

    private const string Columns = "agent_id, region_id, key_id, state, last_start_id, updated";

    private sealed record Row(SessionLocation Location, long LastStartId, bool IsPlaceholder);

    /// <summary>What vouches for an arrival: a handoff (<paramref name="HandoffId"/>) or a start (<paramref name="StartId"/>).</summary>
    private sealed record Match(bool FromVerifiedPlace, string? HandoffId, long? StartId);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A key's statement that it is sending <paramref name="sessionId"/> from its region <paramref name="fromRegion"/> to
    /// <paramref name="toRegion"/> (§8.4, "Handoffs"). Never creates a location.
    /// </summary>
    public async Task<PlaceOutcome> HandoffAsync(RegionKeyGrant grant, Guid handoffId, Guid sessionId, Guid fromRegion, Guid toRegion, HandoffKind kind, CancellationToken ct)
    {
        // §8.4: "A handoff is accepted only if the session's current verified location is a region the key covers."
        if (!grant.Regions.Contains(fromRegion)) return PlaceOutcome.NotCovered;
        try
        {
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

                // §8.4: only from the session's verified location; an unverified one stays so until the next login.
                if (row is not { Location.State: LocationState.Verified } || row.Location.RegionId != fromRegion)
                    return (PlaceOutcome.NotHeld, false);

                // §8.4: "a new handoff replaces any open one for that session" (some cancelled teleports fire no event; Review
                // Focus 1). Marked, not deleted, so a late retry of the old one is recognised (Ruling 9).
                await ExecAsync(conn, tx, $"UPDATE handoffs SET kind = @replaced WHERE session_id = @s AND kind IN {OpenKinds}", ct,
                    ("@replaced", Replaced), ("@s", sessionId.ToString()));
                await ExecAsync(conn, tx, """
                    INSERT INTO handoffs (id, session_id, from_key, from_region, to_region, kind, created)
                    VALUES (@id, @s, @k, @from, @to, @kind, @t)
                    """, ct, ("@id", handoffId.ToString()), ("@s", sessionId.ToString()), ("@k", grant.KeyId), ("@from", fromRegion.ToString()),
                    ("@to", toRegion.ToString()), ("@kind", kind.ToString()), ("@t", Now));

                // §8.4: "teleports to another grid: recorded as 'left the grid'", at once. Its handoff row is only kept so a
                // retry is recognised: arrivals never match that kind.
                if (kind == HandoffKind.OtherGrid)
                    await ExecAsync(conn, tx, "UPDATE session_locations SET region_id = NULL, key_id = NULL, state = @st, updated = @t WHERE session_id = @s", ct,
                        ("@st", LocationState.LeftGrid.ToString()), ("@t", Now), ("@s", sessionId.ToString()));
                return (PlaceOutcome.Ok, true);
            }, ct);
        }
        catch (Exception e) when (IsDatabaseFailure(e))
        {
            return PlaceOutcome.Unavailable;
        }
    }

    /// <summary>
    /// A failed teleport (<c>OnTeleportFail</c>) takes back the open handoffs this key sent for the session from
    /// <paramref name="fromRegion"/> (§8.4); they are marked, so a late retry of one can't reopen it. Nothing for a region
    /// the key doesn't cover. A database failure is <see cref="MoneyUnavailableException"/>.
    /// </summary>
    public async Task CancelHandoffAsync(RegionKeyGrant grant, Guid sessionId, Guid fromRegion, CancellationToken ct)
    {
        if (!grant.Regions.Contains(fromRegion)) return;
        try
        {
            await using var conn = await openMoney(ct);
            await using var cmd = Command(conn, null,
                $"UPDATE handoffs SET kind = @cancelled WHERE session_id = @s AND from_key = @k AND from_region = @from AND kind IN {OpenKinds}",
                ("@cancelled", Cancelled), ("@s", sessionId.ToString()), ("@k", grant.KeyId), ("@from", fromRegion.ToString()));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>
    /// A key's report that <paramref name="agentId"/> became a root agent in its region <paramref name="regionId"/>
    /// (§8.4, "Arrivals"). Placed only when a start or a handoff vouches for it; an unmatched arrival is held for
    /// <see cref="ArrivalWait"/>, since its handoff may still be on the way.
    /// </summary>
    public async Task<ArrivalResult> ArrivedAsync(RegionKeyGrant grant, Guid sessionId, Guid agentId, Guid regionId, CancellationToken ct)
    {
        try
        {
            // §8.4: only Robust's login and the Gatekeeper write Presence, so a row proves the session is real. Nothing is
            // written for a session that isn't logged in (Review Focus 3).
            switch (await presence.HasSessionAsync(agentId, sessionId, ct))
            {
                case PresenceOutcome.Ok: break;
                case PresenceOutcome.NoSession: return Answer(PlaceOutcome.NoSession);
                default: return Answer(PlaceOutcome.Unavailable);
            }
            // §8.4: "accepts it only if the region is covered by the calling key".
            if (!grant.Regions.Contains(regionId)) return Answer(PlaceOutcome.NotCovered);

            var deadline = time.GetUtcNow() + ArrivalWait;
            while (true)
            {
                if (await TryPlaceAsync(grant, sessionId, agentId, regionId, deadline, ct) is { } result) return result;
                // §8.4: "an arrival may get there a moment before its handoff". Hold it and look again in a fresh transaction,
                // so no row lock is held while waiting.
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
    /// Drops the locations of sessions that are not in Presence and haven't changed for <see cref="KeepAbsentFor"/> (§8.4:
    /// "The location record is cleaned up later"), and handoffs past <see cref="HandoffLife"/>. A database failure is
    /// <see cref="MoneyUnavailableException"/>.
    /// </summary>
    public async Task CleanupAsync(CancellationToken ct)
    {
        const int Batch = 500;
        try
        {
            // A session id comes back: teleporting to another grid logs it out of Presence (HGEntityTransferModule), and
            // the Gatekeeper logs it in again with the same id on the way home. Its row, with the start it already used,
            // must outlive Robust's starts (a day), or that start would count again (Ruling 9). So only rows older than
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

    /// <summary>One look for what vouches for an arrival, in one transaction. Null: nothing yet, so hold the arrival.</summary>
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
                // §8.4: "the location becomes that region, verified if the region is verified" and the match came from a
                // verified place (a start, or a handoff from a verified location). Otherwise money is off until next login.
                var verified = match.FromVerifiedPlace && await verification.IsVerifiedAsync(grant, regionId, ct);
                if (match.HandoffId is { } used)
                    await ExecAsync(conn, tx, "UPDATE handoffs SET kind = @used WHERE id = @id", ct, ("@used", Used), ("@id", used));
                await PlaceAsync(conn, tx, sessionId, agentId, regionId, grant.KeyId, verified ? LocationState.Verified : LocationState.Unverified,
                    match.StartId ?? row.LastStartId, ct);
                return (Answer(verified ? PlaceOutcome.Ok : PlaceOutcome.PlacedUnverified), true);
            }

            if (time.GetUtcNow() < deadline) return (null, false);

            // §8.4: "An arrival with no start or handoff, while the avatar has a verified location elsewhere: Refused. The
            // location doesn't change." This is the Neon Pier attack.
            if (here is { State: LocationState.Verified })
                return (new ArrivalResult(PlaceOutcome.Refused, here), false);

            // §8.4: from a region that isn't verified "nobody can vouch for them": unverified, money off until next login.
            await PlaceAsync(conn, tx, sessionId, agentId, regionId, grant.KeyId, LocationState.Unverified, row.LastStartId, ct);
            return (Answer(PlaceOutcome.PlacedUnverified), true);
        }, ct);

    /// <summary>What vouches for an arrival in <paramref name="regionId"/>, in the order §8.4 gives; <paramref name="row"/> is null when the service never placed the session.</summary>
    private async Task<Match?> FindMatchAsync(MySqlConnection conn, MySqlTransaction tx, RegionKeyGrant grant, Guid sessionId, Guid agentId, Guid regionId,
        Row? row, CancellationToken ct)
    {
        var here = row?.Location;

        // 1. §8.4 "Handoffs": an open handoff to this region, younger than HandoffLife. It came from a verified place while
        // the session is still verified where it was handed off from.
        await using (var cmd = Command(conn, tx, $"""
            SELECT id, from_region FROM handoffs
            WHERE session_id = @s AND to_region = @r AND kind IN {OpenKinds} AND created > @since
            ORDER BY created DESC LIMIT 1
            """, ("@s", sessionId.ToString()), ("@r", regionId.ToString()), ("@since", Now - HandoffLife)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct))
                return new Match(here is { State: LocationState.Verified } && here.RegionId == ToGuid(r.GetValue(1)), r.GetValue(0).ToString(), null);

        // A start never moves a verified session (that would let a replay through the Gatekeeper take someone who is still
        // here). It may place an unverified one (Ruling 8): otherwise any key could place a session unverified before the
        // real arrival, and use up the start that should verify it. Only a start newer than last_start_id counts, so each
        // start places once and a used one can't verify the session again.
        var after = row?.LastStartId ?? 0;

        // 2. §8.4 "A login start is accepted once per session": when the session was never placed, or is unverified.
        if ((row is null || here!.State == LocationState.Unverified)
            && await StartAsync(sessionId, agentId, regionId, "login", after, ct) is { } login)
            return new Match(true, null, login);

        // 3. §8.4 A Gatekeeper start counts only in a region of the Grid Asset Server's own (a local program's key, never a
        // paired Region Server's), and only when the session was never placed, last left the grid, or is unverified.
        if (grant.ServerHost is null && (row is null || here!.State is LocationState.LeftGrid or LocationState.Unverified)
            && await StartAsync(sessionId, agentId, regionId, "gatekeeper", after, ct) is { } gatekeeper)
            return new Match(true, null, gatekeeper);

        return null;
    }

    /// <summary>
    /// The newest start Robust wrote for the session in the region, after <paramref name="after"/> and within the last day
    /// by the database's clock (Ruling 9: Robust's own clean-up only runs on logins, so it can't be relied on). A missing
    /// table throws (§8.4: Unavailable).
    /// </summary>
    private async Task<long?> StartAsync(Guid sessionId, Guid agentId, Guid regionId, string source, long after, CancellationToken ct)
    {
        await using var conn = await openRobust(ct);
        await using var cmd = Command(conn, null, """
            SELECT MAX(Id) FROM gridgate_session_starts
            WHERE SessionID = @s AND UserID = @u AND RegionID = @r AND Source = @src AND Id > @after AND At > NOW() - INTERVAL 1 DAY
            """, ("@s", sessionId.ToString()), ("@u", agentId.ToString()), ("@r", regionId.ToString()), ("@src", source), ("@after", after));
        return await cmd.ExecuteScalarAsync(ct) is { } id and not DBNull ? Convert.ToInt64(id) : null;
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

    /// <summary>§8.4: "A missing Robust table or any MySqlException" (or the verification's own database failure) is Unavailable, and nothing changes.</summary>
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

    private static async Task ExecAsync(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>"<paramref name="sql"/> (@p0, @p1, ...)" over <paramref name="ids"/>.</summary>
    private static MySqlCommand InCommand(MySqlConnection conn, string sql, Guid[] ids)
    {
        var cmd = new MySqlCommand(sql + " (" + string.Join(", ", ids.Select((_, i) => "@p" + i)) + ")", conn);
        for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue("@p" + i, ids[i].ToString());
        return cmd;
    }
}
