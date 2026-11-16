using GridGate.Launcher.Core.Settings;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>Why money does or doesn't work in a region (money design §8.2); the website shows each in words.</summary>
public enum RegionStatus { Verified, NotPaired, Moved, NoGridGateMoney, NeedsUpdate, NotHeardFrom }

/// <summary>One region Robust lists, with whether money works there and, if not, why. <paramref name="KeyId"/> is the key that covers it, if any.</summary>
public sealed record RegionState(Guid RegionId, string Name, string ServerIp, int Port, string? KeyId, RegionStatus Status);

/// <summary>
/// Which regions the money service treats as verified (money design §8.2): covered by a key (pinned, for a Region Server),
/// whose program called <c>/money/v1/health</c> recently with a protocol that has handoffs, listing the region. The last
/// health call of each key is kept in the money database. A database failure is <see cref="MoneyUnavailableException"/>.
/// </summary>
public sealed class RegionVerification(Func<CancellationToken, Task<MySqlConnection>> openMoney,
    Func<CancellationToken, Task<MySqlConnection>> openRobust, RegionPins pins, TimeProvider time)
{
    /// <summary>The first money protocol version that includes handoffs. A health call that names none is version 1.</summary>
    public const int HandoffProtocol = 2;

    /// <summary>A key counts as checked in for this long after its last health call (modules call every 3 minutes).</summary>
    public static readonly TimeSpan HeardWithin = TimeSpan.FromMinutes(10);

    private sealed record Heard(int Protocol, HashSet<Guid> Regions, DateTime At);

    /// <summary>Records a health call: the protocol the key's program speaks and the regions it runs. The caller passes only regions the key covers.</summary>
    public async Task HeardAsync(string keyId, int protocol, IReadOnlyCollection<Guid> regions, CancellationToken ct)
    {
        try
        {
            await using var conn = await openMoney(ct);
            await using var cmd = new MySqlCommand(
                "INSERT INTO key_heard (key_id, protocol, regions, at) VALUES (@k, @p, @r, @t) " +
                "ON DUPLICATE KEY UPDATE protocol = @p, regions = @r, at = @t", conn);
            cmd.Parameters.AddWithValue("@k", keyId);
            cmd.Parameters.AddWithValue("@p", protocol);
            cmd.Parameters.AddWithValue("@r", string.Join(",", regions.Distinct()));
            cmd.Parameters.AddWithValue("@t", time.GetUtcNow().UtcDateTime);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>
    /// True when <paramref name="grant"/> covers <paramref name="regionId"/> (a paired server's grant already reflects pins),
    /// its last health call speaks <see cref="HandoffProtocol"/>, came within <see cref="HeardWithin"/> and listed the region.
    /// </summary>
    public async Task<bool> IsVerifiedAsync(RegionKeyGrant grant, Guid regionId, CancellationToken ct) =>
        grant.Regions.Contains(regionId) && StatusOf((await HeardByKeyAsync(ct)).GetValueOrDefault(grant.KeyId), regionId) == RegionStatus.Verified;

    /// <summary>One state per region Robust lists, with the first reason it isn't verified (Moved, Not paired, No GridGate money, Needs an update, Not heard from).</summary>
    public async Task<IReadOnlyList<RegionState>> AllAsync(LauncherSettings settings, CancellationToken ct)
    {
        var rows = await RobustRegionsAsync(ct);
        var servers = (settings.Money.PairedServers ?? []).ToList();
        var own = settings.Regions.Select(r => r.Uuid).ToHashSet();
        var heard = await HeardByKeyAsync(ct);
        var moved = (await pins.MovesAsync(servers, ct)).Select(m => m.RegionId).ToHashSet();
        var covered = new Dictionary<Guid, IReadOnlySet<Guid>>();
        foreach (var server in servers)
            covered[server.Id] = await pins.CoveredAsync(server, server.FirstPort, RegionKeyRing.NextFirstPort(servers, server), own, ct);

        var states = new List<RegionState>();
        foreach (var (id, name, ip, port) in rows)
        {
            string? key = null;
            RegionStatus? status = null;
            if (settings.Regions.FirstOrDefault(r => r.Uuid == id) is { } local)
                key = local.Instance;
            else if (servers.FirstOrDefault(s => string.Equals(s.Host, ip, StringComparison.OrdinalIgnoreCase)
                && port >= s.FirstPort && port < RegionKeyRing.NextFirstPort(servers, s)) is { } server)
            {
                key = "server:" + server.Id.ToString("N");
                if (moved.Contains(id) || !covered[server.Id].Contains(id)) status = RegionStatus.Moved;
            }
            else
                status = RegionStatus.NotPaired;
            states.Add(new RegionState(id, name, ip, port, key, status ?? StatusOf(heard.GetValueOrDefault(key!), id)));
        }
        return states;
    }

    private RegionStatus StatusOf(Heard? heard, Guid regionId)
    {
        if (heard is null) return RegionStatus.NoGridGateMoney;
        if (heard.Protocol < HandoffProtocol) return RegionStatus.NeedsUpdate;
        // At comes back from the database without a kind: it is UTC.
        var at = new DateTimeOffset(DateTime.SpecifyKind(heard.At, DateTimeKind.Utc));
        return time.GetUtcNow() - at > HeardWithin || !heard.Regions.Contains(regionId) ? RegionStatus.NotHeardFrom : RegionStatus.Verified;
    }

    private async Task<Dictionary<string, Heard>> HeardByKeyAsync(CancellationToken ct)
    {
        var found = new Dictionary<string, Heard>();
        try
        {
            await using var conn = await openMoney(ct);
            await using var cmd = new MySqlCommand("SELECT key_id, protocol, regions, at FROM key_heard", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var regions = r.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null).OfType<Guid>().ToHashSet();
                found[r.GetString(0)] = new Heard(r.GetInt32(1), regions, r.GetDateTime(3));
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
            while (await r.ReadAsync(ct))
                if (Guid.TryParse(r.GetValue(0).ToString(), out var id))
                    rows.Add((id, r.GetString(1), r.GetString(2), Convert.ToInt32(r.GetValue(3))));
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
        return rows;
    }
}
