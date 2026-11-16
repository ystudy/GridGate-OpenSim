using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>A purchase's hold (money design §7.2): the buyer's coins wait in Holding until delivery succeeds or fails.</summary>
public sealed record HoldRequest(
    Guid TxnId,
    string Kind,
    AccountRef Buyer,
    AccountRef Seller,
    long Amount,
    Guid? RegionId,
    Guid? ObjectId,
    string? ObjectName,
    string? KeyId = null);

/// <summary>A ledger read that couldn't reach the database.</summary>
public sealed class MoneyUnavailableException(Exception inner) : Exception("The money database can't be reached.", inner);

public sealed partial class MoneyLedger
{
    /// <summary>A hold neither completed nor returned within this time goes back to the buyer on its own.</summary>
    public static readonly TimeSpan HoldLifetime = TimeSpan.FromMinutes(5);

    private sealed record HoldRow(long Id, long BuyerId, long SellerId, long Amount, string Kind, Guid? RegionId, Guid? ObjectId, string? ObjectName, string State, string? KeyId);

    /// <summary>
    /// Moves the buyer's coins into Holding and opens a hold; refused like a payment (freezes, balance, daily limits). The
    /// hold keeps its key, and so do the entries that settle it, but settling takes no key lock: it adds nothing to the
    /// key's spending, and a returned hold stops counting by its own return entry.
    /// </summary>
    public Task<MoneyResult> HoldAsync(HoldRequest r, CancellationToken ct)
    {
        if (!AmountOk(r.Amount) || r.Kind is not ("object" or "land")) return Task.FromResult(MoneyResult.Of(MoneyOutcome.BadAmount));
        if (Same(r.Buyer, r.Seller)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.NotAllowed));
        return Idempotent(r.TxnId, "hold", r, async (conn, tx) =>
        {
            if (r.KeyId is { } key) await LockKeyAsync(conn, tx, key, ct);
            if (await GridFrozenAsync(conn, tx, ct)) return MoneyResult.Of(MoneyOutcome.Frozen);
            var (buyer, r1) = await ResolveAsync(conn, tx, r.Buyer, ct);
            if (buyer is null) return MoneyResult.Of(r1);
            var (seller, r2) = await ResolveAsync(conn, tx, r.Seller, ct);
            if (seller is null) return MoneyResult.Of(r2);

            await using (var insert = new MySqlCommand("""
                INSERT INTO holds (buyer_id, seller_id, amount, kind, region_id, object_id, object_name, created, state, key_id)
                VALUES (@buyer, @seller, @amt, @kind, @region, @object, @name, @now, 'open', @key)
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("@buyer", buyer.Id);
                insert.Parameters.AddWithValue("@seller", seller.Id);
                insert.Parameters.AddWithValue("@amt", r.Amount);
                insert.Parameters.AddWithValue("@kind", r.Kind);
                insert.Parameters.AddWithValue("@region", r.RegionId?.ToString());
                insert.Parameters.AddWithValue("@object", r.ObjectId?.ToString());
                insert.Parameters.AddWithValue("@name", Trim(r.ObjectName, 255));
                insert.Parameters.AddWithValue("@now", Now);
                insert.Parameters.AddWithValue("@key", r.KeyId);
                await insert.ExecuteNonQueryAsync(ct);
                var holdId = insert.LastInsertedId;
                var moved = await MoveBetweenAsync(conn, tx, buyer.Id, MoneySchema.HoldingId, r.Amount,
                    new Entry(EntryType.Hold, r.RegionId, r.ObjectId, r.ObjectName, HoldId: holdId, KeyId: r.KeyId), guarded: true, ct, limited: true);
                return moved.Outcome == MoneyOutcome.Ok ? moved with { HoldId = holdId } : moved;
            }
        }, ct);
    }

    /// <summary>Delivery succeeded: Holding pays the seller. Not blocked by freezes.</summary>
    public Task<MoneyResult> CompleteHoldAsync(Guid txnId, long holdId, CancellationToken ct) =>
        FreezeOnMismatch(Idempotent(txnId, "hold_complete", holdId, (conn, tx) => SettleAsync(conn, tx, holdId, complete: true, ct), ct), ct);

    /// <summary>Delivery failed: Holding pays the buyer back. Not blocked by freezes.</summary>
    public Task<MoneyResult> ReturnHoldAsync(Guid txnId, long holdId, CancellationToken ct) =>
        FreezeOnMismatch(Idempotent(txnId, "hold_return", holdId, (conn, tx) => SettleAsync(conn, tx, holdId, complete: false, ct), ct), ct);

    private const string HoldMismatchReason = "A purchase hold didn't match its entry";

    /// <summary>
    /// A hold that doesn't match its entry means someone changed the books: every payment is frozen, as the self-check would.
    /// A freeze already on keeps its own reason.
    /// </summary>
    private async Task<MoneyResult> FreezeOnMismatch(Task<MoneyResult> settling, CancellationToken ct)
    {
        var result = await settling;
        if (result.Outcome != MoneyOutcome.HoldMismatch) return result;
        try
        {
            if (!(await FrozenAsync(ct)).Frozen)
                await SetFrozenAsync(true, HoldMismatchReason, ct);
        }
        catch (MoneyUnavailableException)
        {
            // The hold stays open and the next settle or self-check tries again.
        }
        return result;
    }

    /// <summary>Returns every open hold older than <see cref="HoldLifetime"/>; how many were returned.</summary>
    public async Task<int> ReturnExpiredHoldsAsync(CancellationToken ct)
    {
        List<long> due = [];
        try
        {
            await using var conn = await open(ct);
            await using var cmd = new MySqlCommand("SELECT id FROM holds WHERE state = 'open' AND created <= @cutoff ORDER BY id", conn);
            cmd.Parameters.AddWithValue("@cutoff", Now - HoldLifetime);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) due.Add(r.GetInt64(0));
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }

        var returned = 0;
        foreach (var id in due)
            if ((await ReturnHoldAsync(Guid.NewGuid(), id, ct)).Outcome == MoneyOutcome.Ok)
                returned++;
        return returned;
    }

    /// <summary>The region a hold was made in, or null for an unknown hold (or one made without a region).</summary>
    public async Task<Guid?> HoldRegionAsync(long holdId, CancellationToken ct)
    {
        try
        {
            await using var conn = await open(ct);
            await using var cmd = new MySqlCommand("SELECT region_id FROM holds WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@id", holdId);
            // MySqlConnector reads CHAR(36) as a Guid.
            return await cmd.ExecuteScalarAsync(ct) is Guid g ? g : null;
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    private async Task<MoneyResult> SettleAsync(MySqlConnection conn, MySqlTransaction tx, long holdId, bool complete, CancellationToken ct)
    {
        var hold = await LockHoldAsync(conn, tx, holdId, ct);
        if (hold is null) return MoneyResult.Of(MoneyOutcome.UnknownHold);
        if (hold.State != "open") return MoneyResult.Of(MoneyOutcome.HoldClosed);
        // Settling isn't guarded and pays holds.amount, so the hold must still be what its entry moved (money review
        // 2026-11-15, L-3): exactly one entry for it, the buyer to Holding for that amount.
        var (entries, matching) = await HoldEntriesAsync(conn, tx, hold, ct);
        if (entries != 1 || matching != 1) return MoneyResult.Of(MoneyOutcome.HoldMismatch);

        var entry = complete
            ? new Entry(EntryType.HoldCompleted, hold.RegionId, hold.ObjectId, hold.ObjectName,
                Reason: hold.Kind == "land" ? EntryType.LandSale : EntryType.ObjectSale, HoldId: hold.Id, KeyId: hold.KeyId)
            : new Entry(EntryType.HoldReturned, hold.RegionId, hold.ObjectId, hold.ObjectName, HoldId: hold.Id, KeyId: hold.KeyId);
        var moved = await MoveBetweenAsync(conn, tx, MoneySchema.HoldingId, complete ? hold.SellerId : hold.BuyerId, hold.Amount, entry, guarded: false, ct);
        // Holding can't go below zero: rolled back with the rest.
        if (moved.FromBalance < 0) return MoneyResult.Of(MoneyOutcome.HoldMismatch);
        await ExecAsync(conn, tx, "UPDATE holds SET state = @state, settled = @now WHERE id = @id",
            ct, ("@state", complete ? "completed" : "returned"), ("@now", Now), ("@id", hold.Id));
        return moved with { HoldId = hold.Id };
    }

    /// <summary>How many entries name the hold, and how many of those are its opening entry as the hold now reads.</summary>
    private static async Task<(long Entries, long Matching)> HoldEntriesAsync(MySqlConnection conn, MySqlTransaction tx, HoldRow hold, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("""
            SELECT COUNT(*), COALESCE(SUM(type = @type AND from_id = @buyer AND to_id = @holding AND amount = @amt), 0)
            FROM entries WHERE hold_id = @id
            """, conn, tx);
        cmd.Parameters.AddWithValue("@type", EntryType.Hold);
        cmd.Parameters.AddWithValue("@buyer", hold.BuyerId);
        cmd.Parameters.AddWithValue("@holding", MoneySchema.HoldingId);
        cmd.Parameters.AddWithValue("@amt", hold.Amount);
        cmd.Parameters.AddWithValue("@id", hold.Id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetInt64(0), Convert.ToInt64(r.GetValue(1)));
    }

    private static async Task<HoldRow?> LockHoldAsync(MySqlConnection conn, MySqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT id, buyer_id, seller_id, amount, kind, region_id, object_id, object_name, state, key_id FROM holds WHERE id = @id FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new HoldRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetGuid(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));
    }
}
