using System.Globalization;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>Whose daily limits: one person's wallet, or one region key (a region program or a paired Region Server).</summary>
public enum LimitKind { Person, Key }

/// <summary>
/// One person or one key the daily limits apply to (verified-regions design §8.7). A person is named by their wallet's
/// <c>accounts.id</c> as text; a key by its grant's <see cref="RegionKeyGrant.KeyId"/> (a program's instance, or
/// <c>server:</c> and the paired server's id), the text <see cref="RegionPins.CarryOverAsync"/> rewrites on a new pairing.
/// </summary>
public sealed record LimitTarget(LimitKind Kind, string Id)
{
    public static LimitTarget ForKey(string keyId) => new(LimitKind.Key, keyId);

    internal static LimitTarget ForAccount(long accountId) => new(LimitKind.Person, accountId.ToString(CultureInfo.InvariantCulture));

    /// <summary>As stored in <c>limit_overrides.kind</c>.</summary>
    internal string KindText => Kind == LimitKind.Person ? "person" : "key";
}

/// <summary>
/// One person's or key's daily limits as they apply now, and what the last 24 hours used of them. <see cref="OwnSpend"/>
/// and <see cref="OwnPayout"/> say the owner set that limit for this one; otherwise it's the grid default.
/// <see cref="Until"/> is when the owner's limits end, if they do.
/// </summary>
public sealed record LimitView(long DailySpend, long DailyPayout, bool OwnSpend, bool OwnPayout, DateTime? Until, long SpentToday, long PaidOutToday);

/// <summary>
/// The daily limits (verified-regions design §8.7). Over the last 24 hours, rolling: a person's spending is what left their
/// wallet in the entry types a region starts (<see cref="SpendTypes"/>), a hold counted when made unless it was returned;
/// a key's spending is the same, by the key that asked; a key's payouts are its script payouts. Staff movements never
/// count. A request with a key locks the key first (<see cref="LockKeyAsync"/>), then the accounts in their usual order,
/// so two requests by one key, or from one wallet, can't both pass a limit.
/// </summary>
public sealed partial class MoneyLedger
{
    /// <summary>The largest daily limit, default or the owner's own: keeps every 24-hour sum far from overflowing.</summary>
    public const long MaxDailyLimit = 100_000_000;

    private static readonly TimeSpan LimitWindow = TimeSpan.FromHours(24);

    /// <summary>What a region takes from a wallet: pay, buy (a hold), parcel passes and fees.</summary>
    private static readonly string[] SpendTypes =
    [
        EntryType.PayAvatar, EntryType.PayObject, EntryType.ParcelPass, EntryType.Hold,
        EntryType.UploadFee, EntryType.ClassifiedFee, EntryType.GroupJoinFee,
    ];

    private static readonly string SpendTypeList = string.Join(", ", SpendTypes.Select(t => "'" + t + "'"));

    /// <summary>The owner's own limits for one person or key, still in force; a null limit is the default.</summary>
    private sealed record LimitOverride(long? DailySpend, long? DailyPayout, DateTime? Until);

    /// <summary>
    /// <paramref name="target"/>'s limits and use now; null for a person without a wallet or a key id that can't be one. A
    /// database failure is <see cref="MoneyUnavailableException"/>.
    /// </summary>
    public Task<LimitView?> LimitsAsync(LimitTarget target, CancellationToken ct) =>
        ReadAsync<LimitView?>(async conn =>
        {
            if (target.Kind == LimitKind.Person)
            {
                if (!long.TryParse(target.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || await ScalarAsync(conn, null, "SELECT COUNT(*) FROM accounts WHERE id = @id AND kind IN ('Person', 'Visitor')", ct, ("@id", id)) == 0)
                    return null;
                return await ViewAsync(conn, null, target, ct);
            }
            return KeyIdOk(target.Id) ? await ViewAsync(conn, null, target, ct) : null;
        }, ct);

    public async Task<LimitView?> PersonLimitsAsync(AccountRef person, CancellationToken ct) =>
        await ReadAsync(conn => WalletIdAsync(conn, person, ct), ct) is { } id ? await LimitsAsync(LimitTarget.ForAccount(id), ct) : null;

    /// <summary>
    /// Sets the owner's own limits for <paramref name="target"/>; a null limit uses the default, and both null removes them.
    /// Each limit must be 0 to <see cref="MaxDailyLimit"/>, <paramref name="until"/> a UTC time (<see cref="DateTimeKind.Utc"/>,
    /// so a form's local time can't move the end by the UTC offset) still ahead, and the staff's name and reason given:
    /// otherwise <see cref="MoneyOutcome.BadAmount"/>. Removing (both limits null) ignores <paramref name="until"/>, so an
    /// end date the page sends back after it has passed can't stop it. A person without a wallet is
    /// <see cref="MoneyOutcome.NoWallet"/>; a key id that can't be one is <see cref="MoneyOutcome.NotAllowed"/>.
    /// </summary>
    public Task<MoneyOutcome> SetLimitsAsync(LimitTarget target, long? dailySpend, long? dailyPayout, DateTime? until, StaffAction staff, CancellationToken ct)
    {
        static bool Ok(long? limit) => limit is null or (>= 0 and <= MaxDailyLimit);
        var removing = dailySpend is null && dailyPayout is null;
        var untilOk = removing || until is null || (until.Value.Kind == DateTimeKind.Utc && until.Value > Now);
        if (!Ok(dailySpend) || !Ok(dailyPayout) || !untilOk || string.IsNullOrWhiteSpace(staff.Reason) || string.IsNullOrWhiteSpace(staff.StaffName))
            return Task.FromResult(MoneyOutcome.BadAmount);
        if (target.Kind == LimitKind.Key && !KeyIdOk(target.Id)) return Task.FromResult(MoneyOutcome.NotAllowed);
        return OutcomeOf(RunAsync(async (conn, tx) =>
        {
            if (target.Kind == LimitKind.Person
                && (!long.TryParse(target.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM accounts WHERE id = @id AND kind IN ('Person', 'Visitor')", ct, ("@id", id)) == 0))
                return MoneyResult.Of(MoneyOutcome.NoWallet);
            if (removing)
                await ExecAsync(conn, tx, "DELETE FROM limit_overrides WHERE kind = @kind AND target = @target", ct,
                    ("@kind", target.KindText), ("@target", target.Id));
            else
                await ExecAsync(conn, tx, """
                    INSERT INTO limit_overrides (kind, target, daily_spend, daily_payout, until, reason, staff, at)
                    VALUES (@kind, @target, @spend, @payout, @until, @reason, @staff, @now)
                    ON DUPLICATE KEY UPDATE daily_spend = @spend, daily_payout = @payout, until = @until, reason = @reason, staff = @staff, at = @now
                    """, ct, ("@kind", target.KindText), ("@target", target.Id), ("@spend", dailySpend), ("@payout", dailyPayout), ("@until", until),
                    ("@reason", Trim(staff.Reason.Trim(), 255)), ("@staff", Trim(staff.StaffName.Trim(), 100)), ("@now", Now));
            return MoneyResult.Of(MoneyOutcome.Ok);
        }, ct));
    }

    public async Task<MoneyOutcome> SetPersonLimitsAsync(AccountRef person, long? dailySpend, long? dailyPayout, DateTime? until, StaffAction staff, CancellationToken ct)
    {
        long? id;
        try
        {
            id = await ReadAsync(conn => WalletIdAsync(conn, person, ct), ct);
        }
        catch (MoneyUnavailableException)
        {
            return MoneyOutcome.Unavailable;
        }
        return id is { } found
            ? await SetLimitsAsync(LimitTarget.ForAccount(found), dailySpend, dailyPayout, until, staff, ct)
            : MoneyOutcome.NoWallet;
    }

    private static async Task<MoneyOutcome> OutcomeOf(Task<MoneyResult> result) => (await result).Outcome;

    /// <summary>A key id fits <c>limit_overrides.target</c> and <c>key_locks.key_id</c>.</summary>
    private static bool KeyIdOk(string? keyId) => keyId is { Length: > 0 and <= 64 };

    /// <summary>The id of <paramref name="who"/>'s wallet, or null; never creates one.</summary>
    private static async Task<long?> WalletIdAsync(MySqlConnection conn, AccountRef who, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT id FROM accounts WHERE agent_id = @agent AND home = @home AND kind IN ('Person', 'Visitor')", conn);
        cmd.Parameters.AddWithValue("@agent", who.AgentId.ToString());
        cmd.Parameters.AddWithValue("@home", NormalHome(who.Home));
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    private async Task<LimitView> ViewAsync(MySqlConnection conn, MySqlTransaction? tx, LimitTarget target, CancellationToken ct)
    {
        var own = await OverrideAsync(conn, tx, target, ct);
        var (spend, payout) = DefaultLimits(target.Kind);
        var person = target.Kind == LimitKind.Person;
        var id = person ? long.Parse(target.Id, CultureInfo.InvariantCulture) : 0;
        return new LimitView(own?.DailySpend ?? spend, own?.DailyPayout ?? payout, own?.DailySpend is not null, own?.DailyPayout is not null, own?.Until,
            person ? await PersonSpentAsync(conn, tx, id, ct) : await KeySpentAsync(conn, tx, target.Id, ct),
            person ? await PersonPaidOutAsync(conn, tx, id, ct) : await KeyPaidOutAsync(conn, tx, target.Id, ct));
    }

    /// <summary>The grid's daily limits for a person (spending, and script payouts per resident) or a key.</summary>
    private (long Spend, long Payout) DefaultLimits(LimitKind kind)
    {
        var s = settings();
        return kind == LimitKind.Person ? (s.DailySpendLimit, s.DailyPayoutLimit) : (s.DailyKeySpendLimit, s.DailyKeyPayoutLimit);
    }

    /// <summary>The owner's limits for <paramref name="target"/>, or null when there are none or their end date has passed.</summary>
    private async Task<LimitOverride?> OverrideAsync(MySqlConnection conn, MySqlTransaction? tx, LimitTarget target, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT daily_spend, daily_payout, until FROM limit_overrides WHERE kind = @kind AND target = @target", conn, tx);
        cmd.Parameters.AddWithValue("@kind", target.KindText);
        cmd.Parameters.AddWithValue("@target", target.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        DateTime? until = r.IsDBNull(2) ? null : DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc);
        if (until <= Now) return null;
        return new LimitOverride(r.IsDBNull(0) ? null : r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), until);
    }

    private async Task<long> SpendLimitAsync(MySqlConnection conn, MySqlTransaction tx, LimitTarget target, CancellationToken ct) =>
        (await OverrideAsync(conn, tx, target, ct))?.DailySpend ?? DefaultLimits(target.Kind).Spend;

    private async Task<long> PayoutLimitAsync(MySqlConnection conn, MySqlTransaction tx, LimitTarget target, CancellationToken ct) =>
        (await OverrideAsync(conn, tx, target, ct))?.DailyPayout ?? DefaultLimits(target.Kind).Payout;

    /// <summary>
    /// Region spending over the last 24 hours from entries matching <paramref name="who"/>. A hold counts when made and stops
    /// counting once it is returned, however late in the window the return comes.
    /// </summary>
    private Task<long> SpentAsync(MySqlConnection conn, MySqlTransaction? tx, string who, object id, CancellationToken ct) =>
        ScalarAsync(conn, tx, $"""
            SELECT COALESCE(SUM(e.amount), 0) FROM entries e
            WHERE {who} AND e.type IN ({SpendTypeList}) AND e.at > @since
              AND NOT (e.type = '{EntryType.Hold}' AND EXISTS (SELECT 1 FROM entries r WHERE r.hold_id = e.hold_id AND r.type = '{EntryType.HoldReturned}'))
            """, ct, ("@id", id), ("@since", Now - LimitWindow));

    private Task<long> PersonSpentAsync(MySqlConnection conn, MySqlTransaction? tx, long accountId, CancellationToken ct) =>
        SpentAsync(conn, tx, "e.from_id = @id", accountId, ct);

    private Task<long> KeySpentAsync(MySqlConnection conn, MySqlTransaction? tx, string keyId, CancellationToken ct) =>
        SpentAsync(conn, tx, "e.key_id = @id", keyId, ct);

    private Task<long> PersonPaidOutAsync(MySqlConnection conn, MySqlTransaction? tx, long accountId, CancellationToken ct) =>
        ScalarAsync(conn, tx, "SELECT COALESCE(SUM(amount), 0) FROM entries WHERE type = @type AND from_id = @id AND at > @since",
            ct, ("@type", EntryType.ScriptPayout), ("@id", accountId), ("@since", Now - LimitWindow));

    private Task<long> KeyPaidOutAsync(MySqlConnection conn, MySqlTransaction? tx, string keyId, CancellationToken ct) =>
        ScalarAsync(conn, tx, "SELECT COALESCE(SUM(amount), 0) FROM entries WHERE type = @type AND key_id = @id AND at > @since",
            ct, ("@type", EntryType.ScriptPayout), ("@id", keyId), ("@since", Now - LimitWindow));

    /// <summary>
    /// Locks <paramref name="keyId"/>'s row in <c>key_locks</c>, creating it the first time, before any account: every
    /// request by one key then runs one at a time. One statement, so the lock is exclusive from the start: an
    /// <c>INSERT IGNORE</c> of an existing row takes a shared lock first, and two requests both waiting to upgrade theirs
    /// would deadlock each other.
    /// </summary>
    private static Task LockKeyAsync(MySqlConnection conn, MySqlTransaction tx, string keyId, CancellationToken ct) =>
        ExecAsync(conn, tx, "INSERT INTO key_locks (key_id) VALUES (@key) ON DUPLICATE KEY UPDATE key_id = key_id", ct, ("@key", keyId));

    /// <summary>
    /// The limits a region's spending must stay within, checked with the payer's account (and the key, if any) locked:
    /// the person's own limit, then the key's. <see cref="MoneyOutcome.Ok"/> when <paramref name="amount"/> fits both.
    /// </summary>
    private async Task<MoneyOutcome> SpendAllowedAsync(MySqlConnection conn, MySqlTransaction tx, long payerId, string? keyId, long amount, CancellationToken ct)
    {
        if (await PersonSpentAsync(conn, tx, payerId, ct) + amount > await SpendLimitAsync(conn, tx, LimitTarget.ForAccount(payerId), ct))
            return MoneyOutcome.OverDailyLimit;
        if (keyId is not null && await KeySpentAsync(conn, tx, keyId, ct) + amount > await SpendLimitAsync(conn, tx, LimitTarget.ForKey(keyId), ct))
            return MoneyOutcome.OverKeyLimit;
        return MoneyOutcome.Ok;
    }

    /// <summary>A key's script payouts must stay within its limit; checked with the key locked.</summary>
    private async Task<MoneyOutcome> KeyPayoutAllowedAsync(MySqlConnection conn, MySqlTransaction tx, string? keyId, long amount, CancellationToken ct) =>
        keyId is not null && await KeyPaidOutAsync(conn, tx, keyId, ct) + amount > await PayoutLimitAsync(conn, tx, LimitTarget.ForKey(keyId), ct)
            ? MoneyOutcome.OverKeyLimit
            : MoneyOutcome.Ok;
}
