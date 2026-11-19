using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// A purchase's hold (money design §7.2): the buyer's coins wait in Holding until delivery succeeds or fails. Kinds
/// <c>object</c> and <c>land</c> pay their <see cref="Seller"/>. A fee held before its action pays Fees:
/// <see cref="EntryType.UploadFee"/> and <see cref="EntryType.ClassifiedFee"/> name no seller; a
/// <see cref="EntryType.GroupJoinFee"/> may name the group's founder, a resident, and pays them when they have a wallet
/// and aren't the buyer (by id, as a charge compares them).
/// </summary>
public sealed record HoldRequest(
    Guid TxnId,
    string Kind,
    AccountRef Buyer,
    AccountRef? Seller,
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

    /// <summary>The fees a region may hold before their action; each pays Fees, or a group's founder for a join fee.</summary>
    private static readonly string[] FeeKinds = [EntryType.UploadFee, EntryType.ClassifiedFee, EntryType.GroupJoinFee];

    /// <summary>Whether a hold of this kind is a fee rather than a purchase.</summary>
    public static bool IsFeeKind(string kind) => FeeKinds.Contains(kind);

    /// <summary>
    /// Moves the buyer's coins into Holding and opens a hold; refused like a payment (freezes, balance, daily limits). The
    /// hold keeps its key, and so do the entries that settle it, but settling takes no key lock: it adds nothing to the
    /// key's spending, and a returned hold stops counting by its own return entry. A fee hold counts toward the limits as
    /// the charge it replaces would, once, when held.
    /// </summary>
    public Task<MoneyResult> HoldAsync(HoldRequest r, CancellationToken ct)
    {
        var fee = IsFeeKind(r.Kind);
        if (!AmountOk(r.Amount) || !fee && r.Kind is not ("object" or "land") || !MoneyRules.FeeAmountOk(r.Kind, r.Amount, settings()))
            return Task.FromResult(MoneyResult.Of(MoneyOutcome.BadAmount));
        // A purchase pays its seller. An upload or classified fee names nobody: it is the grid's. A join fee may name the
        // group's founder, a resident of this grid.
        var payeeOk = r.Seller is not { } named
            ? fee
            : fee ? r.Kind == EntryType.GroupJoinFee && NormalHome(named.Home).Length == 0 : !Same(r.Buyer, named);
        if (!payeeOk) return Task.FromResult(MoneyResult.Of(MoneyOutcome.NotAllowed));
        return Idempotent(r.TxnId, "hold", r, async (conn, tx) =>
        {
            if (r.KeyId is { } key) await LockKeyAsync(conn, tx, key, ct);
            if (await GridFrozenAsync(conn, tx, ct)) return MoneyResult.Of(MoneyOutcome.Frozen);
            var (buyer, r1) = await ResolveAsync(conn, tx, r.Buyer, ct);
            if (buyer is null) return MoneyResult.Of(r1);
            long payeeId;
            if (!fee)
            {
                var (seller, r2) = await ResolveAsync(conn, tx, r.Seller!.Value, ct);
                if (seller is null) return MoneyResult.Of(r2);
                payeeId = seller.Id;
            }
            else
            {
                // As a charge pays it: a founder who is the buyer (by id, whatever the buyer's home) or has no wallet here
                // leaves the join fee to the grid.
                payeeId = MoneySchema.FeesId;
                if (r.Seller is { } founder && founder.AgentId != r.Buyer.AgentId
                    && (await ResolveAsync(conn, tx, AccountRef.Resident(founder.AgentId), ct)).Account is { } founderAccount)
                    payeeId = founderAccount.Id;
            }

            await using (var insert = new MySqlCommand("""
                INSERT INTO holds (buyer_id, seller_id, amount, kind, region_id, object_id, object_name, created, state, key_id)
                VALUES (@buyer, @seller, @amt, @kind, @region, @object, @name, @now, 'open', @key)
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("@buyer", buyer.Id);
                insert.Parameters.AddWithValue("@seller", payeeId);
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
                    new Entry(EntryType.Hold, r.RegionId, r.ObjectId, r.ObjectName, Reason: fee ? r.Kind : null, HoldId: holdId, KeyId: r.KeyId),
                    guarded: true, ct, limited: true);
                return moved.Outcome == MoneyOutcome.Ok ? moved with { HoldId = holdId } : moved;
            }
        }, ct);
    }

    /// <summary>
    /// Delivery succeeded: Holding pays the seller. Not blocked by freezes. A hold the expiry sweep returned meanwhile has its
    /// coins taken from the buyer again (see <see cref="CompleteLateAsync"/>), unless <paramref name="coinsOn"/> is false: with
    /// coins switched off nothing new is taken, and the seller is left unpaid.
    /// </summary>
    public Task<MoneyResult> CompleteHoldAsync(Guid txnId, long holdId, CancellationToken ct, bool coinsOn = true) =>
        FreezeOnMismatch(Idempotent(txnId, "hold_complete", holdId, (conn, tx) => SettleAsync(conn, tx, holdId, complete: true, ct, lateAllowed: coinsOn), ct), ct);

    /// <summary>Delivery failed: Holding pays the buyer back. Not blocked by freezes.</summary>
    public Task<MoneyResult> ReturnHoldAsync(Guid txnId, long holdId, CancellationToken ct) => ReturnHoldAsync(txnId, holdId, expired: false, ct);

    /// <summary>
    /// A return, on request or by the expiry sweep. The sweep's return is marked on its entry (reason
    /// <see cref="ExpiredReason"/>), so a complete that comes late can tell a hold the sweep returned from one the region gave up.
    /// </summary>
    private Task<MoneyResult> ReturnHoldAsync(Guid txnId, long holdId, bool expired, CancellationToken ct) =>
        FreezeOnMismatch(Idempotent(txnId, "hold_return", holdId, (conn, tx) => SettleAsync(conn, tx, holdId, complete: false, ct, expired), ct), ct);

    /// <summary>The reason on a <see cref="EntryType.HoldReturned"/> entry written by the expiry sweep.</summary>
    private const string ExpiredReason = "expired";

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
            if ((await ReturnHoldAsync(Guid.NewGuid(), id, expired: true, ct)).Outcome == MoneyOutcome.Ok)
                returned++;
        return returned;
    }

    /// <summary>Who a hold is between: the buyer's agent id, and the seller's when a person or visitor is paid (null when Fees is).</summary>
    public sealed record HoldParties(Guid Buyer, Guid? Seller);

    /// <summary>The two sides of a hold, or null for an unknown hold. A database failure is <see cref="MoneyUnavailableException"/>.</summary>
    public async Task<HoldParties?> HoldPartiesAsync(long holdId, CancellationToken ct)
    {
        try
        {
            await using var conn = await open(ct);
            await using var cmd = new MySqlCommand("""
                SELECT b.agent_id, s.agent_id, s.kind FROM holds h JOIN accounts b ON b.id = h.buyer_id JOIN accounts s ON s.id = h.seller_id WHERE h.id = @id
                """, conn);
            cmd.Parameters.AddWithValue("@id", holdId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            var seller = r.GetString(2) is nameof(AccountKind.Person) or nameof(AccountKind.Visitor) ? Guid.Parse(r.GetValue(1).ToString()!) : (Guid?)null;
            return new HoldParties(Guid.Parse(r.GetValue(0).ToString()!), seller);
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
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

    /// <summary>
    /// The region and the key a hold was made by, or null for an unknown hold. Either may be null (a hold made before keys
    /// were kept, or without a region). A database failure is <see cref="MoneyUnavailableException"/>.
    /// </summary>
    public async Task<(Guid? RegionId, string? KeyId)?> HoldMadeByAsync(long holdId, CancellationToken ct)
    {
        try
        {
            await using var conn = await open(ct);
            await using var cmd = new MySqlCommand("SELECT region_id, key_id FROM holds WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@id", holdId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            // MySqlConnector reads CHAR(36) as a Guid.
            return (r.IsDBNull(0) ? null : r.GetValue(0) is Guid g ? g : Guid.Parse(r.GetValue(0).ToString()!), r.IsDBNull(1) ? null : r.GetString(1));
        }
        catch (MySqlException e)
        {
            throw new MoneyUnavailableException(e);
        }
    }

    private async Task<MoneyResult> SettleAsync(MySqlConnection conn, MySqlTransaction tx, long holdId, bool complete, CancellationToken ct, bool expired = false, bool lateAllowed = true)
    {
        var hold = await LockHoldAsync(conn, tx, holdId, ct);
        if (hold is null) return MoneyResult.Of(MoneyOutcome.UnknownHold);
        if (hold.State == "returned" && complete && await ReturnedBySweepAsync(conn, tx, hold, ct))
            return lateAllowed ? await CompleteLateAsync(conn, tx, hold, ct) : MoneyResult.Of(MoneyOutcome.HoldClosed) with { SaleUnpaid = true };
        if (hold.State != "open") return MoneyResult.Of(MoneyOutcome.HoldClosed);
        // Settling isn't guarded and pays holds.amount, so the hold must still be what its entry moved (money review
        // 2026-11-15, L-3): exactly one entry for it, the buyer to Holding for that amount.
        var (entries, matching) = await HoldEntriesAsync(conn, tx, hold, ct);
        if (entries != 1 || matching != 1) return MoneyResult.Of(MoneyOutcome.HoldMismatch);

        var reason = IsFeeKind(hold.Kind) ? hold.Kind : hold.Kind == "land" ? EntryType.LandSale : EntryType.ObjectSale;
        var entry = complete
            ? new Entry(EntryType.HoldCompleted, hold.RegionId, hold.ObjectId, hold.ObjectName, Reason: reason, HoldId: hold.Id, KeyId: hold.KeyId)
            : new Entry(EntryType.HoldReturned, hold.RegionId, hold.ObjectId, hold.ObjectName, Reason: expired ? ExpiredReason : null, HoldId: hold.Id, KeyId: hold.KeyId);
        var moved = await MoveBetweenAsync(conn, tx, MoneySchema.HoldingId, complete ? hold.SellerId : hold.BuyerId, hold.Amount, entry, guarded: false, ct);
        // Holding can't go below zero: rolled back with the rest.
        if (moved.FromBalance < 0) return MoneyResult.Of(MoneyOutcome.HoldMismatch);
        await ExecAsync(conn, tx, "UPDATE holds SET state = @state, settled = @now WHERE id = @id",
            ct, ("@state", complete ? "completed" : "returned"), ("@now", Now), ("@id", hold.Id));
        // Fees' balance is nobody's: it never leaves the service.
        return moved with { HoldId = hold.Id, ToBalance = complete && hold.SellerId == MoneySchema.FeesId ? null : moved.ToBalance };
    }

    /// <summary>Whether the expiry sweep returned this hold (not the region, on request) and no late complete has taken its coins since.</summary>
    private static async Task<bool> ReturnedBySweepAsync(MySqlConnection conn, MySqlTransaction tx, HoldRow hold, CancellationToken ct) =>
        await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM entries WHERE hold_id = @id AND type = @returned AND reason = @expired", ct,
            ("@id", hold.Id), ("@returned", EntryType.HoldReturned), ("@expired", ExpiredReason)) == 1
        && await ScalarAsync(conn, tx, "SELECT COUNT(*) FROM entries WHERE hold_id = @id AND type = @late", ct, ("@id", hold.Id), ("@late", EntryType.LateSale)) == 0;

    /// <summary>
    /// A complete for a hold the sweep had already returned: the delivery happened, so the buyer is debited again, as one
    /// new entry straight to the seller, under the hold's id and checked like the hold was (frozen payments, the wallet,
    /// the daily limits of the buyer and the key, the balance). The hold stays returned; the late entry is what says it was
    /// paid. When the buyer can't pay, nothing moves and the answer is <see cref="MoneyOutcome.HoldClosed"/> marked
    /// <see cref="MoneyResult.SaleUnpaid"/>, for the caller to tell staff.
    /// </summary>
    private async Task<MoneyResult> CompleteLateAsync(MySqlConnection conn, MySqlTransaction tx, HoldRow hold, CancellationToken ct)
    {
        MoneyResult Unpaid() => MoneyResult.Of(MoneyOutcome.HoldClosed) with { SaleUnpaid = true };
        if (hold.KeyId is { } key) await LockKeyAsync(conn, tx, key, ct);
        if (await GridFrozenAsync(conn, tx, ct)) return Unpaid();
        var reason = IsFeeKind(hold.Kind) ? hold.Kind : hold.Kind == "land" ? EntryType.LandSale : EntryType.ObjectSale;
        var moved = await MoveBetweenAsync(conn, tx, hold.BuyerId, hold.SellerId, hold.Amount,
            new Entry(EntryType.LateSale, hold.RegionId, hold.ObjectId, hold.ObjectName, Reason: reason, HoldId: hold.Id, KeyId: hold.KeyId),
            guarded: true, ct, limited: true);
        if (moved.Outcome != MoneyOutcome.Ok) return Unpaid();
        return moved with { HoldId = hold.Id, ToBalance = hold.SellerId == MoneySchema.FeesId ? null : moved.ToBalance };
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
