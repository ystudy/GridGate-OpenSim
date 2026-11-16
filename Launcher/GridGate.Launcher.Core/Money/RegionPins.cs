using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>A region Robust lists at one paired server but that is pinned to another: covered by neither.</summary>
public sealed record RegionMove(Guid RegionId, Guid PinnedTo, Guid ListedAt);

/// <summary>
/// Which paired Region Server a region id belongs to. The first time a region id is
/// listed in Robust at a paired server's address and ports it is pinned to that server in the money database; after that
/// only that server's key covers it, however Robust's list changes. A region listed at another server stays uncovered
/// until the owner approves the move; a request that finds it raises a <see cref="AlertKind.RegionMoved"/> alert when
/// <paramref name="alerts"/> is given. A database failure is <see cref="MoneyUnavailableException"/>.
/// </summary>
public sealed class RegionPins(Func<CancellationToken, Task<MySqlConnection>> openMoney, RegionHosts hosts, TimeProvider time, MoneyAlerts? alerts = null)
{
    /// <summary>
    /// The regions Robust lists at <paramref name="server"/>'s address and port range, minus <paramref name="ownRegions"/>,
    /// that are pinned to it. Listed regions nobody has pinned yet are pinned to it first. Only the request path passes
    /// <paramref name="raiseMoves"/>: a status read (the website's region list) raises no alerts.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> CoveredAsync(PairedServer server, int fromPort, int toPort, IReadOnlySet<Guid> ownRegions, CancellationToken ct,
        bool raiseMoves = false)
    {
        var listed = (await hosts.RegionsAtAsync(server.Host, fromPort, toPort, ct)).Where(r => !ownRegions.Contains(r)).ToList();
        if (listed.Count == 0) return new HashSet<Guid>();
        Dictionary<Guid, Guid> pins;
        try
        {
            await using var conn = await openMoney(ct);
            pins = await PinsOfAsync(conn, listed, ct);
            var unpinned = listed.Where(r => !pins.ContainsKey(r)).ToList();
            if (unpinned.Count > 0)
            {
                foreach (var id in unpinned)
                {
                    // IGNORE: two requests may pin the same new region; whichever server's row landed first stands.
                    await Exec(conn, null, "INSERT IGNORE INTO region_pins (region_id, server_id, machine_id, pinned) VALUES (@r, @s, @m, @t)", ct,
                        ("@r", id.ToString()), ("@s", server.Id.ToString("N")), ("@m", (object?)server.MachineId ?? DBNull.Value),
                        ("@t", time.GetUtcNow().UtcDateTime));
                }
                pins = await PinsOfAsync(conn, listed, ct);
            }
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
        // A region listed here but pinned to another server has moved; the owner hears of it, once a day at most.
        // Raising it never fails this answer, nor does the caller hanging up (no token).
        if (raiseMoves && alerts is not null)
            foreach (var moved in listed.Where(r => pins.TryGetValue(r, out var to) && to != server.Id))
                await alerts.RegionMovedAsync(moved, "server:" + server.Id.ToString("N"), CancellationToken.None);
        return listed.Where(r => pins.TryGetValue(r, out var to) && to == server.Id).ToHashSet();
    }

    /// <summary>Regions Robust lists at one paired server that are pinned to a different one (which may no longer be paired).</summary>
    public async Task<IReadOnlyList<RegionMove>> MovesAsync(IReadOnlyList<PairedServer> servers, CancellationToken ct)
    {
        var all = servers.ToList();
        var moves = new List<RegionMove>();
        foreach (var server in all)
        {
            var listed = (await hosts.RegionsAtAsync(server.Host, server.FirstPort, RegionKeyRing.NextFirstPort(all, server), ct)).ToList();
            if (listed.Count == 0) continue;
            try
            {
                await using var conn = await openMoney(ct);
                foreach (var (region, to) in await PinsOfAsync(conn, listed, ct))
                    if (to != server.Id)
                        moves.Add(new RegionMove(region, to, server.Id));
            }
            catch (MySqlException e)
            {
                throw new MoneyUnavailableException(e);
            }
        }
        return moves;
    }

    /// <summary>
    /// Pins <paramref name="regionId"/> to <paramref name="toServerId"/>. Everyone placed in the region was placed under the
    /// server it was pinned to before, which the new server never vouched for: their money pauses until their next login.
    /// The caller audits it.
    /// </summary>
    public async Task ApproveMoveAsync(Guid regionId, Guid toServerId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            await using var conn = await openMoney(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await Exec(conn, tx, "UPDATE region_pins SET server_id = @s, machine_id = NULL, pinned = @t WHERE region_id = @r", ct,
                ("@s", toServerId.ToString("N")), ("@t", now), ("@r", regionId.ToString()));
            await Exec(conn, tx, "UPDATE session_locations SET state = 'Unverified', updated = @t WHERE region_id = @r AND state IN ('Verified', 'Handed')", ct,
                ("@t", now), ("@r", regionId.ToString()));
            await tx.CommitAsync(ct);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>Drops the server's pins, which ends its coverage at once.</summary>
    public Task RemoveServerAsync(Guid serverId, CancellationToken ct) =>
        RunAsync("DELETE FROM region_pins WHERE server_id = @s", ct, ("@s", serverId.ToString("N")));

    /// <summary>
    /// Pairing again: when <paramref name="old"/> and <paramref name="replacement"/> are the same machine (both ids known
    /// and equal) its pins, its key's limit override, the people it placed and the handoffs it sent follow it. A different machine gets none: the old pins stay with
    /// the old server id, so those regions show as moves until the owner approves them.
    /// </summary>
    public async Task CarryOverAsync(PairedServer old, PairedServer replacement, CancellationToken ct)
    {
        if (old.MachineId is null || !string.Equals(old.MachineId, replacement.MachineId, StringComparison.Ordinal)) return;
        try
        {
            await using var conn = await openMoney(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await Exec(conn, tx, "UPDATE region_pins SET server_id = @new, machine_id = @m WHERE server_id = @old", ct,
                ("@new", replacement.Id.ToString("N")), ("@m", replacement.MachineId!), ("@old", old.Id.ToString("N")));
            var (oldKey, newKey) = ("server:" + old.Id.ToString("N"), "server:" + replacement.Id.ToString("N"));
            await Exec(conn, tx, "UPDATE limit_overrides SET target = @new WHERE kind = 'key' AND target = @old", ct, ("@new", newKey), ("@old", oldKey));
            await Exec(conn, tx, "UPDATE session_locations SET key_id = @new WHERE key_id = @old", ct, ("@new", newKey), ("@old", oldKey));
            await Exec(conn, tx, "UPDATE handoffs SET from_key = @new WHERE from_key = @old", ct, ("@new", newKey), ("@old", oldKey));
            await tx.CommitAsync(ct);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    private async Task RunAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        try
        {
            await using var conn = await openMoney(ct);
            await Exec(conn, null, sql, ct, args);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    private static async Task Exec(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<Guid, Guid>> PinsOfAsync(MySqlConnection conn, IReadOnlyList<Guid> regions, CancellationToken ct)
    {
        var pins = new Dictionary<Guid, Guid>();
        await using var cmd = new MySqlCommand { Connection = conn };
        cmd.CommandText = "SELECT region_id, server_id FROM region_pins WHERE region_id IN (" +
            string.Join(",", regions.Select((_, i) => "@r" + i)) + ")";
        for (var i = 0; i < regions.Count; i++) cmd.Parameters.AddWithValue("@r" + i, regions[i].ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (Guid.TryParse(r.GetValue(0).ToString(), out var region) && Guid.TryParseExact(r.GetString(1), "N", out var server))
                pins[region] = server;
        return pins;
    }
}
