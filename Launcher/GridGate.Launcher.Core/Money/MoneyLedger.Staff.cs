using System.Data;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

public sealed partial class MoneyLedger
{
    /// <summary>Staff give coins from the Grid bank.</summary>
    public Task<MoneyResult> GiveAsync(AccountRef to, long amount, StaffAction who, CancellationToken ct) =>
        StaffMoveAsync(who, amount, ct, async (conn, tx) =>
        {
            var (account, refusal) = await ResolveAsync(conn, tx, to, ct, staff: true);
            if (account is null) return MoneyResult.Of(refusal);
            return await MoveBetweenAsync(conn, tx, MoneySchema.BankId, account.Id, amount, StaffEntry(EntryType.StaffGift, who), guarded: true, ct);
        });

    /// <summary>Staff take coins back into the Grid bank; never below zero, but from a frozen account too.</summary>
    public Task<MoneyResult> TakeAsync(AccountRef from, long amount, StaffAction who, CancellationToken ct) =>
        StaffMoveAsync(who, amount, ct, async (conn, tx) =>
        {
            var (account, refusal) = await ResolveAsync(conn, tx, from, ct, staff: true);
            if (account is null) return MoneyResult.Of(refusal);
            return await MoveBetweenAsync(conn, tx, account.Id, MoneySchema.BankId, amount, StaffEntry(EntryType.StaffTake, who), guarded: true, ct, staffTakes: true);
        });

    /// <summary>Moves coins out of Fees to an account.</summary>
    public Task<MoneyResult> MoveFeesAsync(AccountRef to, long amount, StaffAction who, CancellationToken ct) =>
        StaffMoveAsync(who, amount, ct, async (conn, tx) =>
        {
            var (account, refusal) = await ResolveAsync(conn, tx, to, ct, staff: true);
            if (account is null) return MoneyResult.Of(refusal);
            return await MoveBetweenAsync(conn, tx, MoneySchema.FeesId, account.Id, amount, StaffEntry(EntryType.FeeMoved, who), guarded: true, ct);
        });

    /// <summary>
    /// Reverses one entry: the same amount back from its payee to its payer, guarded (from a frozen payee too). An entry
    /// already refunded, or a hold's own movement (settled through the hold), can't be refunded.
    /// </summary>
    public Task<MoneyResult> RefundAsync(long entryId, StaffAction who, CancellationToken ct) =>
        StaffMoveAsync(who, 1, ct, async (conn, tx) =>
        {
            long fromId, toId, amount;
            string type;
            await using (var cmd = new MySqlCommand("SELECT from_id, to_id, amount, type FROM entries WHERE id = @id FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", entryId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return MoneyResult.Of(MoneyOutcome.UnknownEntry);
                (fromId, toId, amount, type) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3));
            }
            if (type is EntryType.Hold or EntryType.HoldCompleted or EntryType.HoldReturned or EntryType.LateSale)
                return MoneyResult.Of(MoneyOutcome.NotAllowed);
            if (await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM entries WHERE reverses = @id", ct, ("@id", entryId)) > 0)
                return MoneyResult.Of(MoneyOutcome.AlreadyRefunded);
            return await MoveBetweenAsync(conn, tx, toId, fromId, amount, StaffEntry(EntryType.Refund, who) with { Reverses = entryId }, guarded: true, ct, staffTakes: true);
        });

    /// <summary>
    /// The owner gives back an upload, classified or group join fee that was taken through a hold. <paramref name="entryId"/> is
    /// the hold's opening entry (what the payer's history shows), its completing entry, or a late sale's. The coins go from
    /// whoever was paid (Fees, or the group's founder) back to <paramref name="payer"/>, and only if that account has them:
    /// otherwise <see cref="MoneyOutcome.NotEnough"/>, with <see cref="MoneyResult.PayeeKind"/> saying who lacks them. Nothing is
    /// taken from the Grid bank, so no coins are made. A fee is refunded once. A fee still held, returned, or paid some other
    /// way, a sale, or one that another person paid is <see cref="MoneyOutcome.NotAllowed"/>.
    /// </summary>
    public Task<MoneyResult> RefundHeldFeeAsync(AccountRef payer, long entryId, StaffAction who, CancellationToken ct) =>
        StaffMoveAsync(who, 1, ct, async (conn, tx) =>
        {
            long? holdId;
            string type;
            await using (var cmd = new MySqlCommand("SELECT hold_id, type FROM entries WHERE id = @id", conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", entryId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return MoneyResult.Of(MoneyOutcome.UnknownEntry);
                (holdId, type) = (r.IsDBNull(0) ? null : r.GetInt64(0), r.GetString(1));
            }
            if (holdId is not { } hold || type is not (EntryType.Hold or EntryType.HoldCompleted or EntryType.LateSale))
                return MoneyResult.Of(MoneyOutcome.NotAllowed);

            // The hold's row is locked first: two refunds of one fee, by different entries of it, wait for each other.
            long buyerId;
            string kind;
            await using (var cmd = new MySqlCommand("SELECT buyer_id, kind FROM holds WHERE id = @id FOR UPDATE", conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", hold);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return MoneyResult.Of(MoneyOutcome.NotAllowed);
                (buyerId, kind) = (r.GetInt64(0), r.GetString(1));
            }
            if (!IsFeeKind(kind)) return MoneyResult.Of(MoneyOutcome.NotAllowed);

            // Only the person whose page this is: the entry's payer.
            if (await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM accounts WHERE id = @id AND agent_id = @agent AND home = @home", ct,
                    ("@id", buyerId), ("@agent", payer.AgentId.ToString()), ("@home", NormalHome(payer.Home))) != 1)
                return MoneyResult.Of(MoneyOutcome.NotAllowed);

            // What paid the fee out: the hold's completing entry, or the late sale that took the coins again.
            long settledId, payeeId, amount;
            await using (var cmd = new MySqlCommand("SELECT id, to_id, amount FROM entries WHERE hold_id = @hold AND type IN (@completed, @late) ORDER BY id LIMIT 1", conn, tx))
            {
                cmd.Parameters.AddWithValue("@hold", hold);
                cmd.Parameters.AddWithValue("@completed", EntryType.HoldCompleted);
                cmd.Parameters.AddWithValue("@late", EntryType.LateSale);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return MoneyResult.Of(MoneyOutcome.NotAllowed);
                (settledId, payeeId, amount) = (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
            }
            if (await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM entries WHERE reverses = @id", ct, ("@id", settledId)) > 0)
                return MoneyResult.Of(MoneyOutcome.AlreadyRefunded);

            var moved = await MoveBetweenAsync(conn, tx, payeeId, buyerId, amount, StaffEntry(EntryType.FeeRefund, who) with { Reverses = settledId },
                guarded: true, ct, staffTakes: true);
            return moved with { PayeeKind = payeeId == MoneySchema.FeesId ? "Fees" : "founder" };
        });

    /// <summary>Moves a visitor's whole balance to their wallet under a new home address, and freezes the old one (a visitor's wallet into a visitor wallet only).</summary>
    public Task<MoneyResult> MergeWalletsAsync(AccountRef from, AccountRef to, StaffAction who, CancellationToken ct) =>
        StaffMoveAsync(who, 1, ct, async (conn, tx) =>
        {
            if (Same(from, to)) return MoneyResult.Of(MoneyOutcome.NotAllowed);
            var old = await FindAsync(conn, tx, from.AgentId, NormalHome(from.Home), ct);
            if (old is null) return MoneyResult.Of(MoneyOutcome.NoWallet);
            // Only a visitor's coins move to a new home address: a resident has no home to move from or to.
            if (old.Kind != AccountKind.Visitor || NormalHome(to.Home).Length == 0) return MoneyResult.Of(MoneyOutcome.NotAVisitor);
            var (target, refusal) = await ResolveAsync(conn, tx, to, ct, staff: true);
            if (target is null) return MoneyResult.Of(refusal);

            var locked = await LockAsync(conn, tx, old.Id, ct);
            var result = new MoneyResult(MoneyOutcome.Ok, 0, target.Balance);
            if (locked.Balance > 0)
                result = await MoveBetweenAsync(conn, tx, old.Id, target.Id, locked.Balance, StaffEntry(EntryType.WalletMerge, who), guarded: false, ct);
            await ExecAsync(conn, tx, "UPDATE accounts SET frozen = 1 WHERE id = @id", ct, ("@id", old.Id));
            return result;
        });

    /// <summary>
    /// The balance of an existing wallet, or <see cref="MoneyOutcome.NoWallet"/>: for staff pages, which must never create a
    /// wallet (or a starting balance) just by being opened. Applies no allowance. A database failure is Unavailable.
    /// </summary>
    public async Task<MoneyResult> FindBalanceAsync(AccountRef who, CancellationToken ct)
    {
        try
        {
            return await ReadAsync(async conn =>
            {
                await using var cmd = new MySqlCommand(
                    "SELECT balance FROM accounts WHERE agent_id = @agent AND home = @home AND kind IN ('Person', 'Visitor')", conn);
                cmd.Parameters.AddWithValue("@agent", who.AgentId.ToString());
                cmd.Parameters.AddWithValue("@home", NormalHome(who.Home));
                return await cmd.ExecuteScalarAsync(ct) is { } v and not DBNull
                    ? new MoneyResult(MoneyOutcome.Ok, Convert.ToInt64(v))
                    : MoneyResult.Of(MoneyOutcome.NoWallet);
            }, ct);
        }
        catch (MoneyUnavailableException)
        {
            return MoneyResult.Unavailable;
        }
    }

    /// <summary>The grid-wide freeze (money design §9.1): every payment is refused; holds still settle.</summary>
    public Task SetFrozenAsync(bool frozen, string reason, CancellationToken ct) =>
        ReadAsync(async conn =>
        {
            await using var cmd = new MySqlCommand(
                "UPDATE state SET value = @on WHERE name = 'frozen'; UPDATE state SET value = @why WHERE name = 'frozen_reason';", conn);
            cmd.Parameters.AddWithValue("@on", frozen ? "1" : "0");
            cmd.Parameters.AddWithValue("@why", Trim(frozen ? reason : "", 500));
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    /// <summary>Freezes or unfreezes one account's debits; nothing happens for an account that doesn't exist yet.</summary>
    public Task SetAccountFrozenAsync(AccountRef who, bool frozen, CancellationToken ct) =>
        ReadAsync(async conn =>
        {
            await using var cmd = new MySqlCommand("UPDATE accounts SET frozen = @on WHERE agent_id = @agent AND home = @home AND kind IN ('Person', 'Visitor')", conn);
            cmd.Parameters.AddWithValue("@on", frozen ? 1 : 0);
            cmd.Parameters.AddWithValue("@agent", who.AgentId.ToString());
            cmd.Parameters.AddWithValue("@home", NormalHome(who.Home));
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);

    /// <summary>An account's switches, or null when it has no wallet yet. A database failure is <see cref="MoneyUnavailableException"/>.</summary>
    public Task<AccountFlags?> AccountFlagsAsync(AccountRef who, CancellationToken ct) =>
        ReadAsync<AccountFlags?>(async conn =>
        {
            await using var cmd = new MySqlCommand(
                "SELECT frozen, payouts_allowed FROM accounts WHERE agent_id = @agent AND home = @home AND kind IN ('Person', 'Visitor')", conn);
            cmd.Parameters.AddWithValue("@agent", who.AgentId.ToString());
            cmd.Parameters.AddWithValue("@home", NormalHome(who.Home));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? new AccountFlags(r.GetInt32(0) != 0, r.GetInt32(1) != 0) : null;
        }, ct);

    public Task<(bool Frozen, string Reason)> FrozenAsync(CancellationToken ct) =>
        ReadAsync(async conn =>
        {
            await using var cmd = new MySqlCommand("SELECT name, value FROM state WHERE name IN ('frozen', 'frozen_reason')", conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var (frozen, reason) = (false, "");
            while (await r.ReadAsync(ct))
                if (r.GetString(0) == "frozen") frozen = r.GetString(1) == "1";
                else reason = r.GetString(1);
            return (frozen, reason);
        }, ct);

    public Task<MoneyTotals> TotalsAsync(CancellationToken ct) =>
        ReadAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var bank = await ScalarAsync(conn, tx, "SELECT balance FROM accounts WHERE id = @id", ct, ("@id", MoneySchema.BankId));
            var residents = await ScalarAsync(conn, tx, "SELECT COALESCE(SUM(balance), 0) FROM accounts WHERE kind = 'Person'", ct);
            var visitors = await ScalarAsync(conn, tx, "SELECT COALESCE(SUM(balance), 0) FROM accounts WHERE kind = 'Visitor'", ct);
            var fees = await ScalarAsync(conn, tx, "SELECT balance FROM accounts WHERE id = @id", ct, ("@id", MoneySchema.FeesId));
            var holding = await ScalarAsync(conn, tx, "SELECT balance FROM accounts WHERE id = @id", ct, ("@id", MoneySchema.HoldingId));
            var today = await ScalarAsync(conn, tx, "SELECT COALESCE(SUM(amount), 0) FROM entries WHERE at >= @day", ct, ("@day", Now.Date));
            await tx.CommitAsync(ct);
            return new MoneyTotals(-bank, residents, visitors, fees, holding, today);
        }, ct);

    /// <summary>The newest <paramref name="max"/> lines of an account's history; empty for an account that doesn't exist.</summary>
    public Task<IReadOnlyList<HistoryLine>> HistoryAsync(AccountRef who, int max, CancellationToken ct) =>
        ReadAsync<IReadOnlyList<HistoryLine>>(async conn =>
        {
            await using var find = new MySqlCommand("SELECT id FROM accounts WHERE agent_id = @agent AND home = @home", conn);
            find.Parameters.AddWithValue("@agent", who.AgentId.ToString());
            find.Parameters.AddWithValue("@home", NormalHome(who.Home));
            if (await find.ExecuteScalarAsync(ct) is not long id) return [];

            await using var cmd = new MySqlCommand("""
                SELECT e.id, e.at, e.type, e.from_id, e.amount, e.from_after, e.to_after, e.object_name, e.reason,
                       o.agent_id, o.home, o.kind
                FROM entries e JOIN accounts o ON o.id = IF(e.from_id = @id, e.to_id, e.from_id)
                WHERE e.from_id = @id OR e.to_id = @id
                ORDER BY e.id DESC LIMIT @max
                """, conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@max", Math.Clamp(max, 1, 1000));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            List<HistoryLine> lines = [];
            while (await r.ReadAsync(ct))
            {
                var outgoing = r.GetInt64(3) == id;
                var kind = Enum.Parse<AccountKind>(r.GetString(11));
                AccountRef? other = kind is AccountKind.Person or AccountKind.Visitor ? new AccountRef(r.GetGuid(9), r.GetString(10)) : null;
                lines.Add(new HistoryLine(r.GetInt64(0), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc)), r.GetString(2), other,
                    outgoing ? -r.GetInt64(4) : r.GetInt64(4), outgoing ? r.GetInt64(5) : r.GetInt64(6),
                    r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
            }
            return lines;
        }, ct);

    private static Entry StaffEntry(string type, StaffAction who) => new(type, Staff: who.StaffName, Reason: who.Reason);

    /// <summary>
    /// A staff movement: a reason and a sensible amount are required. Staff movements aren't refused by the grid-wide freeze,
    /// so the owner can put things right while payments are stopped.
    /// </summary>
    private Task<MoneyResult> StaffMoveAsync(StaffAction who, long amount, CancellationToken ct, Func<MySqlConnection, MySqlTransaction, Task<MoneyResult>> work)
    {
        if (string.IsNullOrWhiteSpace(who.Reason) || string.IsNullOrWhiteSpace(who.StaffName) || !AmountOk(amount))
            return Task.FromResult(MoneyResult.Of(MoneyOutcome.BadAmount));
        return RunAsync(work, ct);
    }

    /// <summary>A read (or a small write outside the movement rules); a database failure is <see cref="MoneyUnavailableException"/>.</summary>
    private async Task<T> ReadAsync<T>(Func<MySqlConnection, Task<T>> work, CancellationToken ct)
    {
        try
        {
            await using var conn = await open(ct);
            return await work(conn);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }
}
