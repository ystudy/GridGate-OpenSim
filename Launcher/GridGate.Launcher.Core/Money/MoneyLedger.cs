using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// The coin ledger (money design §7.1, §7.2): every rule and every SQL statement about coins. Each movement is one database
/// transaction that changes two balances and writes one entry; only the Grid bank may go below zero; a repeated transaction
/// id returns the first answer. Any database failure reads as <see cref="MoneyOutcome.Unavailable"/>, never an exception.
/// </summary>
public sealed partial class MoneyLedger(
    Func<CancellationToken, Task<MySqlConnection>> open,
    Func<MoneySettings> settings,
    Func<MoneyMode> effectiveMode,
    Func<Guid, CancellationToken, Task<bool>> isLocalUser,
    Func<Guid, string, CancellationToken, Task<bool>> isKnownVisitor,
    TimeProvider time) : IMoneyBooks
{
    private const int DeadlockRetries = 3;
    private const int ErDuplicateKey = 1062, ErDeadlock = 1213, ErLockWaitTimeout = 1205, ErCheckRead = 1020;

    private sealed record Account(long Id, AccountKind Kind, long Balance, bool Frozen);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>The balance of <paramref name="who"/> in <see cref="MoneyResult.FromBalance"/>; creates the account if missing.</summary>
    public Task<MoneyResult> BalanceAsync(AccountRef who, CancellationToken ct) =>
        RunAsync(async (conn, tx) =>
        {
            var (account, refusal) = await ResolveAsync(conn, tx, who, ct);
            if (account is null) return MoneyResult.Of(refusal);
            account = await LockAsync(conn, tx, account.Id, ct);
            account = await ApplyAllowanceAsync(conn, tx, account, ct);
            return new MoneyResult(MoneyOutcome.Ok, account.Balance);
        }, ct);

    /// <summary>One avatar paying another (or an object's owner), within the payer's and the key's daily limits.</summary>
    public Task<MoneyResult> TransferAsync(TransferRequest r, CancellationToken ct)
    {
        if (!AmountOk(r.Amount)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.BadAmount));
        if (Same(r.From, r.To)) return Task.FromResult(MoneyResult.Of(MoneyOutcome.NotAllowed));
        return Idempotent(r.TxnId, "transfer", r, async (conn, tx) =>
        {
            if (r.KeyId is { } key) await LockKeyAsync(conn, tx, key, ct);
            return await MoveAsync(conn, tx, r.From, r.To, r.Amount, new Entry(r.Type, r.RegionId, r.ObjectId, r.ObjectName, KeyId: r.KeyId), ct);
        }, ct);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The moving parts the other operations share.

    /// <summary>What an entry records besides the two accounts and the amount.</summary>
    private sealed record Entry(
        string Type,
        Guid? RegionId = null,
        Guid? ObjectId = null,
        string? ObjectName = null,
        string? Staff = null,
        string? Reason = null,
        string? PaymentRef = null,
        long? Reverses = null,
        long? HoldId = null,
        string? KeyId = null);

    private bool AmountOk(long amount) => amount > 0 && amount <= settings().MaxPayment;

    private static bool Same(AccountRef a, AccountRef b) => a.AgentId == b.AgentId && NormalHome(a.Home) == NormalHome(b.Home);

    /// <summary>A visitor's home as stored: trimmed, without a trailing slash, lower case.</summary>
    internal static string NormalHome(string home) => MoneyHomes.Normal(home) ?? UnreadableHome;

    /// <summary>
    /// A home that can't be written in ASCII: a "system:" home, which <see cref="ResolveAsync"/> refuses and no person's
    /// wallet has, so it opens and matches nothing.
    /// </summary>
    private const string UnreadableHome = "system:unreadable";

    /// <summary>Resolves both accounts and moves the coins from <paramref name="from"/> to <paramref name="to"/>, guarded and within the daily limits.</summary>
    private async Task<MoneyResult> MoveAsync(MySqlConnection conn, MySqlTransaction tx, AccountRef from, AccountRef to, long amount, Entry entry, CancellationToken ct)
    {
        if (await GridFrozenAsync(conn, tx, ct)) return MoneyResult.Of(MoneyOutcome.Frozen);
        var (payer, r1) = await ResolveAsync(conn, tx, from, ct);
        if (payer is null) return MoneyResult.Of(r1);
        var (payee, r2) = await ResolveAsync(conn, tx, to, ct);
        if (payee is null) return MoneyResult.Of(r2);
        return await MoveBetweenAsync(conn, tx, payer.Id, payee.Id, amount, entry, guarded: true, ct, limited: true);
    }

    /// <summary>
    /// Locks both accounts in id order, applies any allowance due, then debits (guarded unless <paramref name="guarded"/> is
    /// false: settling a hold), credits and writes the entry. <paramref name="staffTakes"/> is staff taking coins back or
    /// refunding: the balance is still checked, but a frozen account isn't refused, since freezing an account is how staff
    /// stop it spending while they claw coins back. <paramref name="limited"/> is a region
    /// spending from the payer: it must fit the payer's daily limit and, with <see cref="Entry.KeyId"/>, the key's, checked
    /// once both accounts are locked (and the key before them).
    /// </summary>
    private async Task<MoneyResult> MoveBetweenAsync(MySqlConnection conn, MySqlTransaction tx, long fromId, long toId, long amount, Entry entry, bool guarded, CancellationToken ct,
        bool staffTakes = false, bool limited = false)
    {
        var first = await LockAsync(conn, tx, Math.Min(fromId, toId), ct);
        var second = await LockAsync(conn, tx, Math.Max(fromId, toId), ct);
        var payer = await ApplyAllowanceAsync(conn, tx, first.Id == fromId ? first : second, ct);
        var payee = await ApplyAllowanceAsync(conn, tx, first.Id == fromId ? second : first, ct);

        if (guarded)
        {
            if (payer.Frozen && !staffTakes) return MoneyResult.Of(MoneyOutcome.AccountFrozen);
            if (limited && await SpendAllowedAsync(conn, tx, payer.Id, entry.KeyId, amount, ct) is not MoneyOutcome.Ok and var over)
                return MoneyResult.Of(over);
            var debited = await ExecAsync(conn, tx,
                "UPDATE accounts SET balance = balance - @amt WHERE id = @id AND (frozen = 0 OR @staff) AND (kind = 'Bank' OR balance >= @amt)",
                ct, ("@amt", amount), ("@id", payer.Id), ("@staff", staffTakes));
            if (debited != 1) return MoneyResult.Of(MoneyOutcome.NotEnough);
        }
        else
            await ExecAsync(conn, tx, "UPDATE accounts SET balance = balance - @amt WHERE id = @id", ct, ("@amt", amount), ("@id", payer.Id));
        await ExecAsync(conn, tx, "UPDATE accounts SET balance = balance + @amt WHERE id = @id", ct, ("@amt", amount), ("@id", payee.Id));

        // Read back, not computed: an allowance applied above may have moved the bank between the locks and the debit.
        var fromAfter = await ScalarAsync(conn, tx, "SELECT balance FROM accounts WHERE id = @id", ct, ("@id", payer.Id));
        var toAfter = await ScalarAsync(conn, tx, "SELECT balance FROM accounts WHERE id = @id", ct, ("@id", payee.Id));
        var entryId = await WriteEntryAsync(conn, tx, payer.Id, payee.Id, amount, fromAfter, toAfter, entry, ct);
        return new MoneyResult(MoneyOutcome.Ok, fromAfter, toAfter, entryId);
    }

    private async Task<long> WriteEntryAsync(MySqlConnection conn, MySqlTransaction tx, long fromId, long toId, long amount, long fromAfter, long toAfter, Entry e, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO entries (at, type, from_id, to_id, amount, from_after, to_after, region_id, object_id, object_name, staff, reason, payment_ref, reverses, hold_id, key_id)
            VALUES (@at, @type, @from, @to, @amt, @fa, @ta, @region, @object, @name, @staff, @reason, @ref, @rev, @hold, @key)
            """, conn, tx);
        cmd.Parameters.AddWithValue("@at", Now);
        cmd.Parameters.AddWithValue("@type", e.Type);
        cmd.Parameters.AddWithValue("@from", fromId);
        cmd.Parameters.AddWithValue("@to", toId);
        cmd.Parameters.AddWithValue("@amt", amount);
        cmd.Parameters.AddWithValue("@fa", fromAfter);
        cmd.Parameters.AddWithValue("@ta", toAfter);
        cmd.Parameters.AddWithValue("@region", e.RegionId?.ToString());
        cmd.Parameters.AddWithValue("@object", e.ObjectId?.ToString());
        cmd.Parameters.AddWithValue("@name", Trim(e.ObjectName, 255));
        cmd.Parameters.AddWithValue("@staff", Trim(e.Staff, 128));
        cmd.Parameters.AddWithValue("@reason", Trim(e.Reason, 500));
        cmd.Parameters.AddWithValue("@ref", Trim(e.PaymentRef, 128));
        cmd.Parameters.AddWithValue("@rev", e.Reverses);
        cmd.Parameters.AddWithValue("@hold", e.HoldId);
        cmd.Parameters.AddWithValue("@key", e.KeyId);
        await cmd.ExecuteNonQueryAsync(ct);
        return cmd.LastInsertedId;
    }

    /// <summary>
    /// The account for <paramref name="who"/>, creating it when allowed: a resident only if they are a local user (§7.2), a
    /// visitor only while visitor wallets are on and, for a region's request, only for the home the Gatekeeper recorded
    /// (plan 3). OpenSim keeps one GridUser row per UUID, the first home seen, so a visitor whose home grid moved is opened
    /// at the new address by <paramref name="staff"/> (a give or a merge: admin-only, with a reason, audited). While visitor
    /// wallets are off a visitor is <see cref="MoneyOutcome.VisitorsOff"/>, unless staff are acting: staff may still tidy up
    /// an existing wallet, but open none. System accounts can't be named. Doesn't lock.
    /// </summary>
    private async Task<(Account? Account, MoneyOutcome Refusal)> ResolveAsync(MySqlConnection conn, MySqlTransaction tx, AccountRef who, CancellationToken ct,
        bool staff = false)
    {
        var home = NormalHome(who.Home);
        if (who.AgentId == Guid.Empty || home.StartsWith("system:", StringComparison.Ordinal) || home.Length > 255)
            return (null, MoneyOutcome.NoWallet);
        var resident = home.Length == 0;
        var s = settings();
        var visitorsOn = s.EffectiveVisitors != VisitorMoney.Off;
        if (!resident && !staff)
        {
            if (!visitorsOn) return (null, MoneyOutcome.VisitorsOff);
            if (s.EffectiveVisitors == VisitorMoney.Listed && !s.GridListed(home)) return (null, MoneyOutcome.VisitorGridNotListed);
        }

        if (await FindAsync(conn, tx, who.AgentId, home, ct) is { } found)
            return (found, MoneyOutcome.Ok);

        if (resident ? !await isLocalUser(who.AgentId, ct) : !visitorsOn || (!staff && !await isKnownVisitor(who.AgentId, home, ct)))
            return (null, MoneyOutcome.NoWallet);

        var created = await ExecAsync(conn, tx,
            "INSERT IGNORE INTO accounts (agent_id, home, kind, created) VALUES (@agent, @home, @kind, @now)",
            ct, ("@agent", who.AgentId.ToString()), ("@home", home), ("@kind", (resident ? AccountKind.Person : AccountKind.Visitor).ToString()), ("@now", Now));
        var account = await FindAsync(conn, tx, who.AgentId, home, ct)
            ?? throw new InvalidOperationException("An account just created can't be read back.");

        // Only the transaction that created the account gives the starting balance.
        var start = resident ? settings().StartingBalance(effectiveMode()) : 0;
        if (created == 1 && start > 0)
            await MoveBetweenAsync(conn, tx, MoneySchema.BankId, account.Id, start, new Entry(EntryType.StartingBalance), guarded: true, ct);
        return (account, MoneyOutcome.Ok);
    }

    private static async Task<Account?> FindAsync(MySqlConnection conn, MySqlTransaction tx, Guid agent, string home, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT id, kind, balance, frozen FROM accounts WHERE agent_id = @agent AND home = @home", conn, tx);
        cmd.Parameters.AddWithValue("@agent", agent.ToString());
        cmd.Parameters.AddWithValue("@home", home);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    /// <summary>Locks one account row and reads it fresh.</summary>
    private static async Task<Account> LockAsync(MySqlConnection conn, MySqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT id, kind, balance, frozen FROM accounts WHERE id = @id FOR UPDATE", conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"Account {id} is missing.");
        return Read(r);
    }

    private static Account Read(MySqlDataReader r) =>
        new(r.GetInt64(0), Enum.Parse<AccountKind>(r.GetString(1)), r.GetInt64(2), r.GetBoolean(3));

    /// <summary>The weekly allowance, applied when a resident's (locked) account is touched and a week has passed.</summary>
    private async Task<Account> ApplyAllowanceAsync(MySqlConnection conn, MySqlTransaction tx, Account a, CancellationToken ct)
    {
        var weekly = settings().WeeklyAllowance;
        if (weekly <= 0 || a.Kind != AccountKind.Person) return a;

        // One conditional update decides it, so two touches at once can't both grant it (review C2).
        var granted = await ExecAsync(conn, tx,
            "UPDATE accounts SET balance = balance + @amt, last_allowance = @now WHERE id = @id AND (last_allowance IS NULL OR last_allowance <= @due)",
            ct, ("@amt", weekly), ("@now", Now), ("@id", a.Id), ("@due", Now - TimeSpan.FromDays(7)));
        if (granted != 1) return a;
        await ExecAsync(conn, tx, "UPDATE accounts SET balance = balance - @amt WHERE id = @bank", ct, ("@amt", weekly), ("@bank", MoneySchema.BankId));
        var bankAfter = await ScalarAsync(conn, tx, "SELECT balance FROM accounts WHERE id = @bank", ct, ("@bank", MoneySchema.BankId));
        await WriteEntryAsync(conn, tx, MoneySchema.BankId, a.Id, weekly, bankAfter, a.Balance + weekly, new Entry(EntryType.Allowance), ct);
        return a with { Balance = a.Balance + weekly };
    }

    private static async Task<bool> GridFrozenAsync(MySqlConnection conn, MySqlTransaction tx, CancellationToken ct) =>
        await ScalarAsync(conn, tx, "SELECT CAST(value AS SIGNED) FROM state WHERE name = 'frozen'", ct) != 0;

    // ---------------------------------------------------------------------------------------------------------------
    // Transactions, retries and idempotency.

    /// <summary>
    /// Runs <paramref name="work"/> once per <paramref name="txnId"/>: the same request again returns the first answer and
    /// changes nothing; a different request (another operation, or other values) with that id is
    /// <see cref="MoneyOutcome.DuplicateTxn"/> (review M2). Only Ok answers are kept: a refusal changed nothing, so asking
    /// again is harmless.
    /// </summary>
    private Task<MoneyResult> Idempotent(Guid txnId, string op, object request, Func<MySqlConnection, MySqlTransaction, Task<MoneyResult>> work, CancellationToken ct)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(op + "\n" + JsonSerializer.Serialize(request))));
        return RunAsync(async (conn, tx) =>
        {
            if (await StoredAsync(conn, tx, txnId, ct) is { } earlier)
                return earlier.Fingerprint == fingerprint ? earlier.Result : MoneyResult.Of(MoneyOutcome.DuplicateTxn);
            var result = await work(conn, tx);
            if (result.Outcome == MoneyOutcome.Ok)
                await ExecAsync(conn, tx, "INSERT INTO requests (txn_id, op, fingerprint, response, at) VALUES (@txn, @op, @fp, @resp, @now)",
                    ct, ("@txn", txnId.ToString()), ("@op", op), ("@fp", fingerprint), ("@resp", JsonSerializer.Serialize(result)), ("@now", Now));
            return result;
        }, ct, (txnId, fingerprint));
    }

    private static async Task<(string Fingerprint, MoneyResult Result)?> StoredAsync(MySqlConnection conn, MySqlTransaction? tx, Guid txnId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT fingerprint, response FROM requests WHERE txn_id = @txn", conn, tx);
        cmd.Parameters.AddWithValue("@txn", txnId.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), JsonSerializer.Deserialize<MoneyResult>(r.GetString(1))!);
    }

    /// <summary>
    /// One database transaction: committed when <paramref name="work"/> answers Ok, rolled back otherwise. READ COMMITTED,
    /// so every read after a row lock sees what the transaction that held the lock committed (review C1, C2, I1). Deadlocks
    /// are retried; a duplicate transaction id (two copies racing) returns the winner's answer; any other database failure
    /// is <see cref="MoneyOutcome.Unavailable"/>.
    /// </summary>
    private async Task<MoneyResult> RunAsync(Func<MySqlConnection, MySqlTransaction, Task<MoneyResult>> work, CancellationToken ct, (Guid Id, string Fingerprint)? txn = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var conn = await open(ct);
                await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
                var result = await work(conn, tx);
                if (result.Outcome == MoneyOutcome.Ok)
                    await tx.CommitAsync(ct);
                else
                    await tx.RollbackAsync(ct);
                return result;
            }
            catch (MySqlException e) when (e.Number is ErDeadlock or ErLockWaitTimeout or ErCheckRead && attempt < DeadlockRetries)
            {
                await Task.Delay(Random.Shared.Next(5, 30), ct);
            }
            catch (MySqlException e) when (e.Number == ErDuplicateKey && txn is { } t)
            {
                return await ReadStoredAsync(t.Id, ct) is { } stored
                    ? stored.Fingerprint == t.Fingerprint ? stored.Result : MoneyResult.Of(MoneyOutcome.DuplicateTxn)
                    : MoneyResult.Unavailable;
            }
            catch (Exception e) when (e is MySqlException or TimeoutException or InvalidOperationException { Source: "MySqlConnector" })
            {
                return MoneyResult.Unavailable;
            }
        }
    }

    private async Task<(string Fingerprint, MoneyResult Result)?> ReadStoredAsync(Guid txnId, CancellationToken ct)
    {
        try
        {
            await using var conn = await open(ct);
            return await StoredAsync(conn, null, txnId, ct);
        }
        catch (MySqlException)
        {
            return null;
        }
    }

    private static async Task<int> ExecAsync(MySqlConnection conn, MySqlTransaction tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await using var cmd = new MySqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    /// <summary>Cuts <paramref name="s"/> to <paramref name="max"/> UTF-16 units without splitting a surrogate pair.</summary>
    private static string? Trim(string? s, int max)
    {
        if (s is null || s.Length <= max) return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut];
    }
}
