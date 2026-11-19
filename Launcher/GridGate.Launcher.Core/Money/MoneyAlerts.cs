using System.Data;
using GridGate.Launcher.Core.Logs;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>What an owner's money alert is about.</summary>
public enum AlertKind
{
    UnhandedArrival,
    KeyLimitReached,
    RegionMoved,
    PersonLimitRepeated,

    /// <summary>A sale was completed after its hold ran out, and the buyer couldn't pay it again: the seller wasn't paid.</summary>
    SaleNotPaid,

    /// <summary>One key's script payouts in a day went to more than <see cref="MoneyRules.PayoutOwnersAlertAbove"/> different owners.</summary>
    ManyPayoutOwners,

    /// <summary>A pairing took over an older paired server's address and first port: the new server's key, with the old one in the other key.</summary>
    ServerTookOver,
}

/// <summary>A notice asks the owner to check something; a warning shows on the dashboard too.</summary>
public enum AlertLevel { Notice, Warning }

/// <summary>
/// One alert row: <paramref name="Count"/> times since <paramref name="FirstAt"/>, the last at <paramref name="LastAt"/> (UTC).
/// <paramref name="KeyId"/> is the key that raised the last one; <paramref name="AgentId"/> the last person, when there is one.
/// For an unhanded arrival, <paramref name="OtherKeyId"/> is the other key that had the person verified, when there was one
/// (the two disagree, and either may be wrong), and <paramref name="Repeated"/> says the row is a warning because of the
/// claiming key's own repeated claims, the only kind of warning that offers to remove its server.
/// </summary>
public sealed record MoneyAlert(long Id, AlertKind Kind, AlertLevel Level, string KeyId, Guid? RegionId, Guid? AgentId, int Count, DateTime FirstAt, DateTime LastAt,
    string? OtherKeyId = null, bool Repeated = false);

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
/// The owner's money alerts, in the money database's <c>alerts</c> table. Each kind is grouped, per
/// <see cref="Window"/> from a row's first time, into one row that counts up: an unhanded arrival per key and region (a
/// disagreement with another key on a row apart from plain claims), a key's limit per key, a person's limit per person, a sale left unpaid per key and region, a key's payouts to many owners per key.
/// A moved region has one open row per region until it is cleared,
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

    /// <summary>Unhanded arrivals from one key within <see cref="Window"/> that make a warning: three or more in a day.</summary>
    public const int WarningAt = 3;

    /// <summary>A person's daily spending limit hit this often within <see cref="Window"/> appears as a notice.</summary>
    public const int PersonLimitAt = 3;

    /// <summary>At most this many open alerts are read.</summary>
    public const int MaxOpen = 500;

    /// <summary>Past this many remembered moves, the ones older than <see cref="Window"/> are dropped.</summary>
    private const int MaxMovesSeen = 4096;

    private const string Columns = "id, kind, level, key_id, region_id, agent_id, count, first_at, last_at, other_key_id, repeated";

    /// <summary>The level one more alert calls for; <paramref name="Repeated"/> when it is a warning by the key's own count, <paramref name="OtherKey"/> the other key involved.</summary>
    private sealed record Level(AlertLevel Value, bool Repeated = false, string? OtherKey = null);

    private static Task<Level> Fixed(AlertLevel level) => Task.FromResult(new Level(level));

    // The money service is the only writer: one alert at a time, so two at once never open two rows for one group.
    private readonly SemaphoreSlim _one = new(1, 1);

    // Moves raised lately, by region and the key listing it. A move is found on every request from that server, and a
    // repeat must cost no database work and never wait for _one.
    private readonly Dictionary<(Guid Region, string Key), DateTime> _movesSeen = [];

    // Keys alerted lately for paying out for many owners, so the payouts after the first cost no database work.
    private readonly Dictionary<string, DateTime> _ownersSeen = [];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// <paramref name="keyId"/>'s region <paramref name="regionId"/> claimed <paramref name="agentId"/> arrived with no start or
    /// handoff. A notice, since a handoff lost despite the retries looks the same; a warning from the <see cref="WarningAt"/>th
    /// from one key within <see cref="Window"/> (repeated claims), or at once when the person's verified place
    /// (<paramref name="verifiedKeyElsewhere"/>) is another key's: then the two keys disagree, and either may be the wrong one.
    /// A disagreement is never counted as one of the claimer's repeated claims, and is kept on rows of its own, apart from
    /// the plain claims, so a row's count and its "repeated" mark say what they are about.
    /// </summary>
    public Task UnhandedArrivalAsync(string keyId, Guid regionId, Guid agentId, string? verifiedKeyElsewhere, CancellationToken ct) =>
        RaiseAsync(AlertKind.UnhandedArrival, keyId, regionId, agentId, async (conn, tx) =>
        {
            var other = verifiedKeyElsewhere is not null && verifiedKeyElsewhere != keyId ? verifiedKeyElsewhere : null;
            // A disagreement raises its own warning naming both keys, and the honest one may be either of them.
            if (other is not null) return new Level(AlertLevel.Warning, Repeated: false, other);
            // The key's plain claims across its regions, cleared rows too: clearing a notice doesn't reset the day's count. A
            // row seen within the window counts whole, so the count rolls; it may reach a little past a day, which fails safe.
            var before = await ScalarAsync(conn, tx, """
                SELECT COALESCE(SUM(count), 0) FROM alerts WHERE kind = @kind AND key_id = @key AND other_key_id IS NULL AND last_at > @since
                """, ct, ("@kind", nameof(AlertKind.UnhandedArrival)), ("@key", keyId), ("@since", Now - Window));
            var repeated = before + 1 >= WarningAt;
            return new Level(repeated ? AlertLevel.Warning : AlertLevel.Notice, repeated);
        }, ct);

    /// <summary><paramref name="keyId"/> reached its daily region limit or its daily payout limit: a warning.</summary>
    public Task KeyLimitReachedAsync(string keyId, CancellationToken ct) =>
        RaiseAsync(AlertKind.KeyLimitReached, keyId, null, null, (_, _) => Fixed(AlertLevel.Warning), ct);

    /// <summary>
    /// Region <paramref name="regionId"/> is listed at the server of <paramref name="toKeyId"/> but pinned to another:
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
        return RaiseAsync(AlertKind.RegionMoved, toKeyId, regionId, null, (_, _) => Fixed(AlertLevel.Notice), ct,
            failed: () => { lock (_movesSeen) _movesSeen.Remove((regionId, toKeyId)); });
    }

    /// <summary>
    /// <paramref name="agentId"/> hit their daily spending limit in <paramref name="keyId"/>'s region. Counted; it appears as a
    /// notice from the <see cref="PersonLimitAt"/>th within <see cref="Window"/>.
    /// </summary>
    public Task PersonLimitHitAsync(Guid agentId, string keyId, CancellationToken ct) =>
        RaiseAsync(AlertKind.PersonLimitRepeated, keyId, null, agentId, (_, _) => Fixed(AlertLevel.Notice), ct);

    /// <summary>
    /// A sale in <paramref name="regionId"/>, made through <paramref name="keyId"/>, was completed after its hold ran out and
    /// <paramref name="buyer"/> couldn't pay it again, so the seller wasn't paid: a warning, counted per key and region.
    /// </summary>
    public Task SaleNotPaidAsync(string keyId, Guid regionId, Guid? buyer, CancellationToken ct) =>
        RaiseAsync(AlertKind.SaleNotPaid, keyId, regionId, buyer, (_, _) => Fixed(AlertLevel.Warning), ct);

    /// <summary>
    /// <paramref name="keyId"/>'s script payouts in the last day went to more than
    /// <see cref="MoneyRules.PayoutOwnersAlertAbove"/> different owners: a warning, once per key per <see cref="Window"/>.
    /// A repeat within it does nothing at all.
    /// </summary>
    public Task ManyPayoutOwnersAsync(string keyId, CancellationToken ct)
    {
        var now = Now;
        lock (_ownersSeen)
        {
            if (_ownersSeen.TryGetValue(keyId, out var seen) && now - seen < Window) return Task.CompletedTask;
            if (_ownersSeen.Count >= MaxMovesSeen)
                foreach (var old in _ownersSeen.Where(m => now - m.Value >= Window).Select(m => m.Key).ToList())
                    _ownersSeen.Remove(old);
            _ownersSeen[keyId] = now;
        }
        return RaiseAsync(AlertKind.ManyPayoutOwners, keyId, null, null, (_, _) => Fixed(AlertLevel.Warning), ct, once: true,
            failed: () => { lock (_ownersSeen) _ownersSeen.Remove(keyId); });
    }

    /// <summary>
    /// <paramref name="newKeyId"/>'s pairing took over the address and first port of <paramref name="oldKeyId"/>'s server, which is no longer paired:
    /// a notice, one row per new server, so the owner can check it was theirs.
    /// </summary>
    public Task ServerTookOverAsync(string newKeyId, string oldKeyId, CancellationToken ct) =>
        RaiseAsync(AlertKind.ServerTookOver, newKeyId, null, null, (_, _) => Task.FromResult(new Level(AlertLevel.Notice, false, oldKeyId)), ct);

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
                        r.IsDBNull(5) ? null : ToGuid(r.GetValue(5)), r.GetInt32(6), Utc(r.GetDateTime(7)), Utc(r.GetDateTime(8)),
                        r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10) != 0));
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
        Func<MySqlConnection, MySqlTransaction, Task<Level>> levelOf, CancellationToken ct, Action? failed = null, bool once = false)
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
                // A once-a-day alert: any row for this key in the window, cleared or not, means it was raised already.
                if (once && await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM alerts WHERE kind = @kind AND key_id = @key AND first_at > @since", ct,
                        ("@kind", kind.ToString()), ("@key", keyId), ("@since", now - Window)) > 0)
                {
                    await tx.RollbackAsync(ct);
                    return;
                }
                var called = await levelOf(conn, tx);
                var level = called.Value;
                var args = new (string, object?)[]
                {
                    ("@kind", kind.ToString()), ("@key", keyId), ("@region", regionId?.ToString()), ("@agent", agentId?.ToString()), ("@since", now - Window),
                    ("@other", called.OtherKey), ("@repeated", called.Repeated ? 1 : 0),
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
                      region_id = COALESCE(@region, region_id), agent_id = COALESCE(@agent, agent_id),
                      other_key_id = COALESCE(@other, other_key_id), repeated = GREATEST(repeated, @repeated) WHERE id = @id AND cleared IS NULL
                    """, ct, [.. args, ("@level", next.ToString()), ("@now", now), ("@id", existing)]) == 1;
                if (counted)
                    change = (false, next > oldLevel, count + 1, next);
                else
                {
                    await ExecAsync(conn, tx, """
                        INSERT INTO alerts (kind, level, key_id, region_id, agent_id, count, first_at, last_at, other_key_id, repeated)
                        VALUES (@kind, @level, @key, @region, @agent, 1, @now, @now, @other, @repeated)
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
        // Disagreements (another key named) and plain claims keep rows apart.
        AlertKind.UnhandedArrival => "key_id = @key AND region_id = @region AND (other_key_id IS NULL) = (@other IS NULL)",
        AlertKind.KeyLimitReached => "key_id = @key",
        AlertKind.RegionMoved => "region_id = @region",
        AlertKind.PersonLimitRepeated => "agent_id = @agent",
        AlertKind.SaleNotPaid => "key_id = @key AND region_id = @region",
        AlertKind.ManyPayoutOwners => "key_id = @key",
        AlertKind.ServerTookOver => "key_id = @key",
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
