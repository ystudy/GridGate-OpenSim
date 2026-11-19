using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>Why money does or doesn't work in a region; the website shows each in words. Waiting: first seen at a server (when it paired or later), until the owner confirms it.</summary>
public enum RegionStatus { Verified, NotPaired, Moved, NoGridGateMoney, NeedsUpdate, NotHeardFrom, Waiting }

/// <summary>One region Robust lists, with whether money works there and, if not, why. <paramref name="KeyId"/> is the key that covers it, if any; for a moved region <paramref name="PinnedKeyId"/> is the key of the server it is pinned to; for a waiting one <paramref name="FirstSeen"/> is when it first appeared at its server (UTC), when known.</summary>
public sealed record RegionState(Guid RegionId, string Name, string ServerIp, int Port, string? KeyId, RegionStatus Status, string? PinnedKeyId = null, DateTime? FirstSeen = null);

/// <summary>
/// A key's last health call, for the Region Servers list: the money protocol its module speaks, when it
/// called (UTC), and what that makes it: <see cref="RegionStatus.Verified"/>, <see cref="RegionStatus.NeedsUpdate"/> or
/// <see cref="RegionStatus.NotHeardFrom"/>. A key that never called has none.
/// </summary>
public sealed record KeyHeard(int Protocol, DateTime At, RegionStatus Status);

/// <summary>
/// Which regions the money service treats as verified: covered by a key (pinned, for a Region Server), whose program called
/// <c>/money/v1/health</c> recently with a protocol that has handoffs, listing the region. Each region's last health call is
/// kept on its own, by key and region, since several programs of one Region Server share its key and each lists only its
/// own regions; so is each key's last call, for the Region Servers list. A database failure is
/// <see cref="MoneyUnavailableException"/>.
/// </summary>
public sealed class RegionVerification(Func<CancellationToken, Task<MySqlConnection>> openMoney,
    Func<CancellationToken, Task<MySqlConnection>> openRobust, RegionPins pins, TimeProvider time)
{
    /// <summary>The first money protocol version that includes handoffs. A health call that names none is version 1.</summary>
    public const int HandoffProtocol = 2;

    /// <summary>A key counts as checked in for this long after its last health call (modules call every 3 minutes).</summary>
    public static readonly TimeSpan HeardWithin = TimeSpan.FromMinutes(10);

    /// <summary>A health call heard: the money protocol it spoke, and when (UTC).</summary>
    private sealed record Heard(int Protocol, DateTime At);

    /// <summary>What was heard from one key: its last health call, and each region's.</summary>
    private sealed record KeyRecord(Heard? Key, Dictionary<Guid, Heard> Regions);

    /// <summary>
    /// Records a health call: the protocol the key's program speaks and the regions it runs. The caller passes only regions
    /// the key covers. Each region listed is heard now; regions another program of the same key runs keep their own record.
    /// </summary>
    public async Task HeardAsync(string keyId, int protocol, IReadOnlyCollection<Guid> regions, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            await using var conn = await openMoney(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var cmd = new MySqlCommand(
                "INSERT INTO key_heard (key_id, protocol, at) VALUES (@k, @p, @t) ON DUPLICATE KEY UPDATE protocol = @p, at = @t", conn, tx))
            {
                cmd.Parameters.AddWithValue("@k", keyId);
                cmd.Parameters.AddWithValue("@p", protocol);
                cmd.Parameters.AddWithValue("@t", now);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            var list = regions.Distinct().ToList();
            if (list.Count > 0)
            {
                await using var cmd = new MySqlCommand(
                    "INSERT INTO region_heard (key_id, region_id, protocol, at) VALUES " +
                    string.Join(", ", list.Select((_, i) => $"(@k, @r{i}, @p, @t)")) +
                    " ON DUPLICATE KEY UPDATE protocol = VALUES(protocol), at = VALUES(at)", conn, tx);
                cmd.Parameters.AddWithValue("@k", keyId);
                cmd.Parameters.AddWithValue("@p", protocol);
                cmd.Parameters.AddWithValue("@t", now);
                for (var i = 0; i < list.Count; i++) cmd.Parameters.AddWithValue("@r" + i, list[i].ToString());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>
    /// True when <paramref name="grant"/> covers <paramref name="regionId"/> (a paired server's grant already reflects pins),
    /// and that key's last health call listing the region speaks <see cref="HandoffProtocol"/> and came within
    /// <see cref="HeardWithin"/>.
    /// </summary>
    public async Task<bool> IsVerifiedAsync(RegionKeyGrant grant, Guid regionId, CancellationToken ct) =>
        grant.Regions.Contains(regionId) && StatusOf((await HeardByKeyAsync(grant.KeyId, ct)).GetValueOrDefault(grant.KeyId), regionId) == RegionStatus.Verified;

    /// <summary>One state per region Robust lists, with the first reason it isn't verified (Moved, Waiting, Not paired, No GridGate money, Needs an update, Not heard from).</summary>
    public async Task<IReadOnlyList<RegionState>> AllAsync(LauncherSettings settings, CancellationToken ct)
    {
        var rows = await RobustRegionsAsync(ct);
        var servers = (settings.Money.PairedServers ?? []).ToList();
        var own = settings.Regions.Select(r => r.Uuid).ToHashSet();
        var heard = await HeardByKeyAsync(null, ct);
        var moved = (await pins.MovesAsync(servers, ct)).ToDictionary(m => m.RegionId, m => m.PinnedTo);
        var covered = new Dictionary<Guid, IReadOnlySet<Guid>>();
        var waiting = new Dictionary<Guid, IReadOnlyDictionary<Guid, DateTime>>();
        foreach (var server in servers)
        {
            covered[server.Id] = await pins.CoveredAsync(server, server.FirstPort, RegionKeyRing.NextFirstPort(servers, server), own, ct, pinNew: false);
            waiting[server.Id] = await pins.WaitingSinceAsync(server.Id, ct);
        }

        var states = new List<RegionState>();
        foreach (var (id, name, ip, port) in rows)
        {
            string? key = null;
            RegionStatus? status = null;
            string? pinnedKey = null;
            DateTime? firstSeen = null;
            if (settings.Regions.FirstOrDefault(r => r.Uuid == id) is { } local)
                key = local.Instance;
            else if (servers.FirstOrDefault(s => string.Equals(s.Host, ip, StringComparison.OrdinalIgnoreCase)
                && port >= s.FirstPort && port < RegionKeyRing.NextFirstPort(servers, s)) is { } server)
            {
                key = "server:" + server.Id.ToString("N");
                if (moved.TryGetValue(id, out var pinnedTo))
                {
                    status = RegionStatus.Moved;
                    pinnedKey = "server:" + pinnedTo.ToString("N");
                }
                else if (waiting[server.Id].TryGetValue(id, out var since))
                {
                    status = RegionStatus.Waiting;
                    firstSeen = since;
                }
                else if (!covered[server.Id].Contains(id))
                    status = RegionStatus.Waiting; // listed here, pinned to nobody yet: it waits for the owner once the server's next call pins it
            }
            else
                status = RegionStatus.NotPaired;
            states.Add(new RegionState(id, name, ip, port, key, status ?? StatusOf(heard.GetValueOrDefault(key!), id), pinnedKey, firstSeen));
        }
        return states;
    }

    /// <summary>Every key's last health call, by key id. A database failure is <see cref="MoneyUnavailableException"/>.</summary>
    public async Task<IReadOnlyDictionary<string, KeyHeard>> KeysAsync(CancellationToken ct)
    {
        var keys = new Dictionary<string, KeyHeard>();
        foreach (var (key, record) in await HeardByKeyAsync(null, ct))
            if (record.Key is { } heard)
                keys[key] = new KeyHeard(heard.Protocol, Utc(heard.At),
                    heard.Protocol < HandoffProtocol ? RegionStatus.NeedsUpdate : Recent(heard) ? RegionStatus.Verified : RegionStatus.NotHeardFrom);
        return keys;
    }

    /// <summary>
    /// What the key's health calls make <paramref name="regionId"/>: no call at all is no GridGate money. The region's own last
    /// listing decides: it must speak handoffs and be recent. A region never listed needs an update when the key's last call
    /// is older than handoffs, and is not heard from otherwise. Another program sharing the key never changes it.
    /// </summary>
    private RegionStatus StatusOf(KeyRecord? heard, Guid regionId)
    {
        if (heard is null) return RegionStatus.NoGridGateMoney;
        if (!heard.Regions.TryGetValue(regionId, out var region))
            return heard.Key is { Protocol: < HandoffProtocol } ? RegionStatus.NeedsUpdate : RegionStatus.NotHeardFrom;
        if (region.Protocol < HandoffProtocol) return RegionStatus.NeedsUpdate;
        return Recent(region) ? RegionStatus.Verified : RegionStatus.NotHeardFrom;
    }

    private bool Recent(Heard heard) => time.GetUtcNow() - new DateTimeOffset(Utc(heard.At)) <= HeardWithin;

    /// <summary>At comes back from the database without a kind: it is UTC.</summary>
    private static DateTime Utc(DateTime at) => DateTime.SpecifyKind(at, DateTimeKind.Utc);

    /// <summary>What was heard from each key (or only <paramref name="keyId"/>), by key id.</summary>
    private async Task<Dictionary<string, KeyRecord>> HeardByKeyAsync(string? keyId, CancellationToken ct)
    {
        var found = new Dictionary<string, KeyRecord>();
        KeyRecord Of(string key) => found.TryGetValue(key, out var k) ? k : found[key] = new KeyRecord(null, []);
        var where = keyId is null ? "" : " WHERE key_id = @k";
        try
        {
            await using var conn = await openMoney(ct);
            await using (var cmd = new MySqlCommand("SELECT key_id, protocol, at FROM key_heard" + where, conn))
            {
                if (keyId is not null) cmd.Parameters.AddWithValue("@k", keyId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var key = r.GetString(0);
                    found[key] = Of(key) with { Key = new Heard(r.GetInt32(1), r.GetDateTime(2)) };
                }
            }
            await using (var cmd = new MySqlCommand("SELECT key_id, region_id, protocol, at FROM region_heard" + where, conn))
            {
                if (keyId is not null) cmd.Parameters.AddWithValue("@k", keyId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    if (Guid.TryParse(r.GetValue(1).ToString(), out var region))
                        Of(r.GetString(0)).Regions[region] = new Heard(r.GetInt32(2), r.GetDateTime(3));
            }
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
        return found;
    }

    private async Task<List<(Guid Id, string Name, string Ip, int Port)>> RobustRegionsAsync(CancellationToken ct)
    {
        var rows = new List<(Guid, string, string, int)>();
        try
        {
            await using var conn = await openRobust(ct);
            await using var cmd = new MySqlCommand("SELECT uuid, regionName, serverIP, serverPort FROM regions ORDER BY regionName", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            // Robust's columns may be NULL: a region with no name or address is listed with an empty one, never a failure.
            while (await r.ReadAsync(ct))
                if (Guid.TryParse(r.GetValue(0).ToString(), out var id))
                    rows.Add((id, r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3))));
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
        return rows;
    }
}
