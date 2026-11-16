using System.Data;
using GridGate.Launcher.Core.Logs;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>What an owner's money alert is about (money design §8.8).</summary>
public enum AlertKind { UnhandedArrival, KeyLimitReached, RegionMoved, PersonLimitRepeated }

/// <summary>A notice asks the owner to check something; only a warning offers "Remove this server" (§8.8).</summary>
public enum AlertLevel { Notice, Warning }

/// <summary>
/// One alert row: <paramref name="Count"/> times since <paramref name="FirstAt"/>, the last at <paramref name="LastAt"/> (UTC).
/// <paramref name="KeyId"/> is the key that raised the last one; <paramref name="AgentId"/> the last person, when there is one.
/// </summary>
public sealed record MoneyAlert(long Id, AlertKind Kind, AlertLevel Level, string KeyId, Guid? RegionId, Guid? AgentId, int Count, DateTime FirstAt, DateTime LastAt);

/// <summary>The open alerts, as the dashboard and the Money page read and clear them.</summary>
public interface IMoneyAlerts
{
    /// <summary>The open alerts, notices and warnings, newest first. A database failure is <see cref="MoneyUnavailableException"/>.</summary>
    Task<IReadOnlyList<MoneyAlert>> OpenAsync(CancellationToken ct);

    /// <summary>
    /// The open warnings only, newest first, so no backlog of notices can push one out of the read. A database failure is
    /// <see cref="MoneyUnavailableException"/>.
    /// </summary>
    Task<IReadOnlyList<MoneyAlert>> WarningsAsync(CancellationToken ct);

    /// <summary>Closes alert <paramref name="id"/>, recording when and by whom; the row stays. A database failure is <see cref="MoneyUnavailableException"/>.</summary>
    Task ClearAsync(long id, string staff, CancellationToken ct);
}

/// <summary>
/// The owner's money alerts (money design §8.8), in the money database's <c>alerts</c> table. Each kind is grouped, per
/// <see cref="Window"/> from a row's first time, into one row that counts up: an unhanded arrival per key and region, a
/// key's limit per key, a person's limit per person. A moved region has one open row per region until it is cleared,
/// counting moves seen (another server listing it), not requests. A row's level only rises, from notice to warning. The
/// audit log gets a line when a row opens (for a person's limit, when it appears) and when it becomes a warning, not on
/// every count.
/// <para>Raising an alert never fails the answer that raised it: any failure is logged by its type and the alert is lost.
/// Refused claims are only counted here; they never slow down or block the key.</para>
/// </summary>
public sealed class MoneyAlerts(Func<CancellationToken, Task<MySqlConnection>> openMoney, TimeProvider time, ILogger? log = null, AuditLog? audit = null) : IMoneyAlerts
{
    /// <summary>A row counts what happens within this long of its first time; after that the next one starts a new row.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Unhanded arrivals from one key within <see cref="Window"/> that make a warning (§8.8: "three or more in a day").</summary>
    public const int WarningAt = 3;

    /// <summary>A person's daily spending limit hit this often within <see cref="Window"/> appears as a notice (§8.8).</summary>
    public const int PersonLimitAt = 3;

    /// <summary>At most this many open alerts are read.</summary>
    public const int MaxOpen = 500;

    /// <summary>Past this many remembered moves, the ones older than <see cref="Window"/> are dropped.</summary>
    private const int MaxMovesSeen = 4096;

    private const string Columns = "id, kind, level, key_id, region_id, agent_id, count, first_at, last_at";

    // The money service is the only writer: one alert at a time, so two at once never open two rows for one group.
    private readonly SemaphoreSlim _one = new(1, 1);

    // Moves raised lately, by region and the key listing it. A move is found on every request from that server, and a
    // repeat must cost no database work and never wait for _one.
    private readonly Dictionary<(Guid Region, string Key), DateTime> _movesSeen = [];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// <paramref name="keyId"/>'s region <paramref name="regionId"/> claimed <paramref name="agentId"/> arrived with no start or
    /// handoff (§8.4). A notice, since a handoff lost despite the retries looks the same; a warning from the
    /// <see cref="WarningAt"/>th from one key within <see cref="Window"/>, or at once when the person's verified place
    /// (<paramref name="verifiedKeyElsewhere"/>) is another key's.
    /// </summary>
    public Task UnhandedArrivalAsync(string keyId, Guid regionId, Guid agentId, string? verifiedKeyElsewhere, CancellationToken ct) =>
        RaiseAsync(AlertKind.UnhandedArrival, keyId, regionId, agentId, async (conn, tx) =>
        {
            if (verifiedKeyElsewhere is not null && verifiedKeyElsewhere != keyId) return AlertLevel.Warning;
            // Across the key's regions, cleared rows too: clearing a notice doesn't reset the day's count. A row seen within
            // the window counts whole, so the count rolls; it may reach a little past a day, which fails safe.
            var before = await ScalarAsync(conn, tx, "SELECT COALESCE(SUM(count), 0) FROM alerts WHERE kind = @kind AND key_id = @key AND last_at > @since", ct,
                ("@kind", nameof(AlertKind.UnhandedArrival)), ("@key", keyId), ("@since", Now - Window));
            return before + 1 >= WarningAt ? AlertLevel.Warning : AlertLevel.Notice;
        }, ct);

    /// <summary><paramref name="keyId"/> reached its daily region limit or its daily payout limit: a warning.</summary>
    public Task KeyLimitReachedAsync(string keyId, CancellationToken ct) =>
        RaiseAsync(AlertKind.KeyLimitReached, keyId, null, null, (_, _) => Task.FromResult(AlertLevel.Warning), ct);

    /// <summary>
    /// Region <paramref name="regionId"/> is listed at the server of <paramref name="toKeyId"/> but pinned to another (§8.3):
    /// a notice. Raised at most once per region and listing key per <see cref="Window"/>; a repeat within it does nothing at
    /// all. An open row for the region counts up only when another key lists it now, so its count is moves seen.
    /// </summary>
    public Task RegionMovedAsync(Guid regionId, string toKeyId, CancellationToken ct)
    {
        var now = Now;
        lock (_movesSeen)
        {
            if (_movesSeen.TryGetValue((regionId, toKeyId), out var seen) && now - seen < Window) return Task.CompletedTask;
            if (_movesSeen.Count >= MaxMovesSeen)
                foreach (var old in _movesSeen.Where(m => now - m.Value >= Window).Select(m => m.Key).ToList())
                    _movesSeen.Remove(old);
            _movesSeen[(regionId, toKeyId)] = now;
        }
        // Not written: the next request tries again.
        return RaiseAsync(AlertKind.RegionMoved, toKeyId, regionId, null, (_, _) => Task.FromResult(AlertLevel.Notice), ct,
            failed: () => { lock (_movesSeen) _movesSeen.Remove((regionId, toKeyId)); });
    }

    /// <summary>
    /// <paramref name="agentId"/> hit their daily spending limit in <paramref name="keyId"/>'s region. Counted; it appears as a
    /// notice from the <see cref="PersonLimitAt"/>th within <see cref="Window"/>.
    /// </summary>
    public Task PersonLimitHitAsync(Guid agentId, string keyId, CancellationToken ct) =>
        RaiseAsync(AlertKind.PersonLimitRepeated, keyId, null, agentId, (_, _) => Task.FromResult(AlertLevel.Notice), ct);

    public Task<IReadOnlyList<MoneyAlert>> OpenAsync(CancellationToken ct) => ReadOpenAsync(null, ct);

    public Task<IReadOnlyList<MoneyAlert>> WarningsAsync(CancellationToken ct) => ReadOpenAsync(AlertLevel.Warning, ct);

    private async Task<IReadOnlyList<MoneyAlert>> ReadOpenAsync(AlertLevel? only, CancellationToken ct)
    {
        try
        {
            await using var conn = await openMoney(ct);
            await using var cmd = Command(conn, null,
                "SELECT " + Columns + " FROM alerts WHERE cleared IS NULL AND NOT (kind = @person AND count < @at)" + (only is null ? "" : " AND level = @level") +
                " ORDER BY last_at DESC, id DESC LIMIT " + MaxOpen,
                ("@person", nameof(AlertKind.PersonLimitRepeated)), ("@at", PersonLimitAt), ("@level", only?.ToString()));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var open = new List<MoneyAlert>();
            while (await r.ReadAsync(ct))
                if (Enum.TryParse<AlertKind>(r.GetString(1), out var kind) && Enum.TryParse<AlertLevel>(r.GetString(2), out var level))
                    open.Add(new MoneyAlert(r.GetInt64(0), kind, level, r.GetString(3), r.IsDBNull(4) ? null : ToGuid(r.GetValue(4)),
                        r.IsDBNull(5) ? null : ToGuid(r.GetValue(5)), r.GetInt32(6), Utc(r.GetDateTime(7)), Utc(r.GetDateTime(8))));
            return open;
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    public async Task ClearAsync(long id, string staff, CancellationToken ct)
    {
        try
        {
            await using var conn = await openMoney(ct);
            await using var cmd = Command(conn, null, "UPDATE alerts SET cleared = @now, cleared_by = @staff WHERE id = @id AND cleared IS NULL",
                ("@now", Now), ("@staff", staff.Length > 100 ? staff[..100] : staff), ("@id", id));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    /// <summary>
    /// Counts one more <paramref name="kind"/> on its group's open row, or opens one. <paramref name="levelOf"/> is the level
    /// this one calls for; the row keeps the higher of that and its own. Never throws: a failure is logged by its type, and
    /// <paramref name="failed"/> runs.
    /// </summary>
    private async Task RaiseAsync(AlertKind kind, string keyId, Guid? regionId, Guid? agentId,
        Func<MySqlConnection, MySqlTransaction, Task<AlertLevel>> levelOf, CancellationToken ct, Action? failed = null)
    {
        var held = false;
        try
        {
            await _one.WaitAsync(ct);
            held = true;
            var now = Now;
            (bool Opened, bool Rose, int Count, AlertLevel Level) change;
            await using (var conn = await openMoney(ct))
            await using (var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct))
            {
                var level = await levelOf(conn, tx);
                var args = new (string, object?)[]
                {
                    ("@kind", kind.ToString()), ("@key", keyId), ("@region", regionId?.ToString()), ("@agent", agentId?.ToString()), ("@since", now - Window),
                };
                long? id = null;
                var (oldLevel, count, oldKey) = (AlertLevel.Notice, 0, "");
                // A moved region has one open row until it is cleared, whatever its age; the rest count within the window.
                var within = kind == AlertKind.RegionMoved ? "" : "first_at > @since AND ";
                await using (var cmd = Command(conn, tx,
                    $"SELECT id, level, count, key_id FROM alerts WHERE kind = @kind AND cleared IS NULL AND {within}{GroupOf(kind)} ORDER BY id DESC LIMIT 1", args))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                    if (await r.ReadAsync(ct))
                        (id, oldLevel, count, oldKey) = (r.GetInt64(0), Enum.TryParse<AlertLevel>(r.GetString(1), out var l) ? l : AlertLevel.Notice, r.GetInt32(2), r.GetString(3));

                // The same move seen again (after a restart, say) isn't another move.
                if (id is not null && kind == AlertKind.RegionMoved && oldKey == keyId)
                {
                    await tx.RollbackAsync(ct);
                    return;
                }
                var next = level > oldLevel ? level : oldLevel;
                // ClearAsync doesn't wait for _one: a row cleared since it was read isn't counted, and this one opens a new row.
                var counted = id is { } existing && await ExecAsync(conn, tx, """
                    UPDATE alerts SET count = count + 1, level = @level, last_at = @now, key_id = @key,
                      region_id = COALESCE(@region, region_id), agent_id = COALESCE(@agent, agent_id) WHERE id = @id AND cleared IS NULL
                    """, ct, [.. args, ("@level", next.ToString()), ("@now", now), ("@id", existing)]) == 1;
                if (counted)
                    change = (false, next > oldLevel, count + 1, next);
                else
                {
                    await ExecAsync(conn, tx, """
                        INSERT INTO alerts (kind, level, key_id, region_id, agent_id, count, first_at, last_at)
                        VALUES (@kind, @level, @key, @region, @agent, 1, @now, @now)
                        """, ct, [.. args, ("@level", level.ToString()), ("@now", now)]);
                    change = (true, false, 1, level);
                }
                await tx.CommitAsync(ct);
            }
            // A person's limit only appears at PersonLimitAt, so that is when it opens for the owner.
            var opened = kind == AlertKind.PersonLimitRepeated ? change.Count == PersonLimitAt : change.Opened;
            if (opened || change.Rose)
                audit?.Write(new AuditEntry("GridGate", null, "system", null, "money-alert",
                    $"{kind} key {keyId}" + (regionId is { } r1 ? $" region {r1}" : "") + (agentId is { } a1 ? $" person {a1}" : ""),
                    change.Level == AlertLevel.Warning ? "warning" : "notice"));
        }
        catch (Exception e)
        {
            // Whatever went wrong (the database, a cancelled wait), the answer that raised this alert stands.
            failed?.Invoke();
            log?.LogError("A money alert ({Kind}) couldn't be written ({Error}); the answer it came with stands.", kind, e.GetType().Name);
        }
        finally
        {
            if (held) _one.Release();
        }
    }

    /// <summary>The columns one kind's rows are grouped by (besides the kind and the window).</summary>
    private static string GroupOf(AlertKind kind) => kind switch
    {
        AlertKind.UnhandedArrival => "key_id = @key AND region_id = @region",
        AlertKind.KeyLimitReached => "key_id = @key",
        AlertKind.RegionMoved => "region_id = @region",
        AlertKind.PersonLimitRepeated => "agent_id = @agent",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DateTime Utc(DateTime t) => DateTime.SpecifyKind(t, DateTimeKind.Utc);

    /// <summary>CHAR(36) columns read as <see cref="Guid"/> with MySqlConnector's default settings, as text otherwise.</summary>
    private static Guid ToGuid(object value) => value is Guid g ? g : Guid.Parse(value.ToString()!);

    private static async Task<long> ScalarAsync(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> ExecAsync(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static MySqlCommand Command(MySqlConnection conn, MySqlTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
