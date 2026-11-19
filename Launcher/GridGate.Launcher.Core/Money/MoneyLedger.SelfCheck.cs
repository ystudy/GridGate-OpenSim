using System.Data;
using MySqlConnector;

namespace GridGate.Launcher.Core.Money;

public sealed partial class MoneyLedger
{
    /// <summary>How many lines of one kind of problem the self-check reports; the first ones are enough to start from.</summary>
    private const int ProblemsPerCheck = 20;

    private static readonly string[] Wallets = [nameof(AccountKind.Person), nameof(AccountKind.Visitor)];
    private static readonly string[] Bank = [nameof(AccountKind.Bank)];
    private static readonly string[] Fees = [nameof(AccountKind.Fees)];
    private static readonly string[] Holding = [nameof(AccountKind.Holding)];
    private static readonly string[] Residents = [nameof(AccountKind.Person)];
    private static readonly string[] NotHolding = [.. Wallets, .. Bank, .. Fees];

    /// <summary>
    /// Which accounts each entry type moves coins between, and what it must carry: a staff name, a hold, the entry it
    /// reverses. Everything the ledger writes fits one of these (money review 2026-11-15, L-3); a type not listed here,
    /// such as <see cref="EntryType.ObjectSale"/> (only ever a hold's reason), is never written.
    /// </summary>
    private sealed record EntryRule(string[] From, string[] To, bool Staff = false, bool Hold = false, bool Reverses = false);

    private static readonly Dictionary<string, EntryRule> EntryRules = new()
    {
        [EntryType.StartingBalance] = new(Bank, Residents),
        [EntryType.Allowance] = new(Bank, Residents),
        [EntryType.BoughtCoins] = new(Bank, Wallets),
        [EntryType.StaffGift] = new(Bank, Wallets, Staff: true),
        [EntryType.StaffTake] = new(Wallets, Bank, Staff: true),
        [EntryType.PayAvatar] = new(Wallets, Wallets),
        [EntryType.PayObject] = new(Wallets, Wallets),
        [EntryType.ParcelPass] = new(Wallets, Wallets),
        [EntryType.ScriptPayout] = new(Residents, Wallets),
        [EntryType.UploadFee] = new(Wallets, Fees),
        [EntryType.ClassifiedFee] = new(Wallets, Fees),
        [EntryType.GroupJoinFee] = new(Wallets, [.. Fees, .. Residents]),
        [EntryType.Hold] = new(Wallets, Holding, Hold: true),
        // A fee held before its action completes into Fees; the hold check below says which holds may.
        [EntryType.HoldCompleted] = new(Holding, [.. Wallets, .. Fees], Hold: true),
        [EntryType.HoldReturned] = new(Holding, Wallets, Hold: true),
        // A sale completed after the sweep returned its hold: the buyer to the seller (or Fees); the hold check below says which.
        [EntryType.LateSale] = new(Wallets, [.. Wallets, .. Fees], Hold: true),
        [EntryType.Refund] = new(NotHolding, NotHolding, Staff: true, Reverses: true),
        [EntryType.WalletMerge] = new(Wallets, Wallets, Staff: true),
        [EntryType.FeeMoved] = new(Fees, Wallets, Staff: true),
        // The owner gave back a fee taken through a hold: from whoever was paid, never from the bank or Holding.
        [EntryType.FeeRefund] = new([.. Fees, .. Residents], Wallets, Staff: true, Reverses: true),
    };

    /// <summary>
    /// Checks the books from one consistent snapshot. When they don't balance, or anything else is wrong, freezes every
    /// payment with the reason "Self-check found a difference" and returns what differs.
    /// </summary>
    public async Task<SelfCheckResult> SelfCheckAsync(CancellationToken ct)
    {
        var result = await ReadAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            var sum = await ScalarAsync(conn, tx, "SELECT COALESCE(SUM(balance), 0) FROM accounts", ct);
            var mismatches = await BalanceMismatchesAsync(conn, tx, ct);
            List<string> problems = [];
            await AccountProblemsAsync(conn, tx, problems, ct);
            await HoldProblemsAsync(conn, tx, problems, ct);
            await RunningBalanceProblemsAsync(conn, tx, problems, ct);
            await EntryTypeProblemsAsync(conn, tx, problems, ct);
            await RefundProblemsAsync(conn, tx, problems, ct);
            await tx.CommitAsync(ct);
            return new SelfCheckResult(sum == 0 && mismatches.Count == 0 && problems.Count == 0, sum, mismatches, problems);
        }, ct);
        if (!result.Ok)
            await SetFrozenAsync(true, "Self-check found a difference", ct);
        return result;
    }

    /// <summary>Each account's stored balance against the sum of its entries.</summary>
    private static async Task<List<BalanceMismatch>> BalanceMismatchesAsync(MySqlConnection conn, MySqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("""
            SELECT a.id, a.balance, COALESCE(i.s, 0) - COALESCE(o.s, 0) AS computed
            FROM accounts a
            LEFT JOIN (SELECT to_id, SUM(amount) s FROM entries GROUP BY to_id) i ON i.to_id = a.id
            LEFT JOIN (SELECT from_id, SUM(amount) s FROM entries GROUP BY from_id) o ON o.from_id = a.id
            HAVING a.balance <> computed
            ORDER BY a.id
            """, conn, tx);
        List<BalanceMismatch> mismatches = [];
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            mismatches.Add(new BalanceMismatch(r.GetInt64(0), r.GetInt64(1), Convert.ToInt64(r.GetValue(2))));
        return mismatches;
    }

    /// <summary>
    /// Only the Grid bank goes below zero, and the bank, Fees and Holding are the three fixed system accounts: a wallet
    /// relabelled as a bank could spend without limit.
    /// </summary>
    private static async Task AccountProblemsAsync(MySqlConnection conn, MySqlTransaction tx, List<string> problems, CancellationToken ct)
    {
        await ReadLinesAsync(conn, tx, problems, $"""
            SELECT id, balance FROM accounts WHERE id <> {MoneySchema.BankId} AND balance < 0 ORDER BY id LIMIT {ProblemsPerCheck}
            """, r => $"Account {r.GetInt64(0)} is below zero ({r.GetInt64(1)}).", ct);
        await ReadLinesAsync(conn, tx, problems, $"""
            SELECT id, kind FROM accounts
            WHERE NOT ((id = {MoneySchema.BankId} AND kind = 'Bank') OR (id = {MoneySchema.FeesId} AND kind = 'Fees')
                       OR (id = {MoneySchema.HoldingId} AND kind = 'Holding')
                       OR (id > {MoneySchema.HoldingId} AND ((kind = 'Person' AND home = '')
                                                            OR (kind = 'Visitor' AND home <> '' AND home NOT LIKE 'system:%'))))
            ORDER BY id LIMIT {ProblemsPerCheck}
            """, r => $"Account {r.GetInt64(0)} has the wrong kind ({r.GetString(1)}).", ct);
    }

    /// <summary>
    /// Holding holds exactly the open holds, and each hold still reads as the entries that opened and settled it: one
    /// <c>hold</c> entry from the buyer for its amount, and once settled, one entry paying that amount to the seller or back
    /// to the buyer. Only a fee's hold pays Fees, an upload or classified fee's hold pays nobody else, and a group join
    /// fee's hold pays Fees or a resident other than the buyer.
    /// </summary>
    private static async Task HoldProblemsAsync(MySqlConnection conn, MySqlTransaction tx, List<string> problems, CancellationToken ct)
    {
        var holding = await ScalarAsync(conn, tx, "SELECT balance FROM accounts WHERE id = @id", ct, ("@id", MoneySchema.HoldingId));
        var open = await ScalarAsync(conn, tx, "SELECT COALESCE(SUM(amount), 0) FROM holds WHERE state = 'open'", ct);
        if (holding != open)
            problems.Add($"Holding has {holding} but its open holds add up to {open}.");

        await ReadLinesAsync(conn, tx, problems, $"""
            SELECT h.id FROM holds h
            WHERE h.state NOT IN ('open', 'completed', 'returned')
               OR (h.kind IN ('{EntryType.UploadFee}', '{EntryType.ClassifiedFee}') AND h.seller_id <> {MoneySchema.FeesId})
               OR (h.kind NOT IN ('{EntryType.UploadFee}', '{EntryType.ClassifiedFee}', '{EntryType.GroupJoinFee}') AND h.seller_id = {MoneySchema.FeesId})
               OR (h.kind = '{EntryType.GroupJoinFee}' AND (h.seller_id = h.buyer_id
                     OR (h.seller_id <> {MoneySchema.FeesId} AND NOT EXISTS (SELECT 1 FROM accounts a WHERE a.id = h.seller_id AND a.kind = 'Person'))))
               OR (SELECT COUNT(*) FROM entries e WHERE e.hold_id = h.id AND e.type <> '{EntryType.LateSale}') <> IF(h.state = 'open', 1, 2)
               OR (SELECT COUNT(*) FROM entries e WHERE e.hold_id = h.id AND e.type = '{EntryType.LateSale}') > IF(h.state = 'returned', 1, 0)
               OR EXISTS (SELECT 1 FROM entries e WHERE e.hold_id = h.id AND e.type = '{EntryType.LateSale}'
                     AND NOT (e.from_id = h.buyer_id AND e.to_id = h.seller_id AND e.amount = h.amount))
               OR (SELECT COUNT(*) FROM entries e WHERE e.hold_id = h.id AND e.type = '{EntryType.Hold}'
                     AND e.from_id = h.buyer_id AND e.to_id = {MoneySchema.HoldingId} AND e.amount = h.amount) <> 1
               OR (h.state = 'completed' AND NOT EXISTS (SELECT 1 FROM entries e WHERE e.hold_id = h.id AND e.type = '{EntryType.HoldCompleted}'
                     AND e.from_id = {MoneySchema.HoldingId} AND e.to_id = h.seller_id AND e.amount = h.amount))
               OR (h.state = 'returned' AND NOT EXISTS (SELECT 1 FROM entries e WHERE e.hold_id = h.id AND e.type = '{EntryType.HoldReturned}'
                     AND e.from_id = {MoneySchema.HoldingId} AND e.to_id = h.buyer_id AND e.amount = h.amount))
            ORDER BY h.id LIMIT {ProblemsPerCheck}
            """, r => $"Hold {r.GetInt64(0)} doesn't match its entries.", ct);
        await ReadLinesAsync(conn, tx, problems, $"""
            SELECT e.id, e.hold_id FROM entries e LEFT JOIN holds h ON h.id = e.hold_id
            WHERE e.hold_id IS NOT NULL AND h.id IS NULL ORDER BY e.id LIMIT {ProblemsPerCheck}
            """, r => $"Entry {r.GetInt64(0)} names hold {r.GetInt64(1)}, which doesn't exist.", ct);
    }

    /// <summary>
    /// Every entry records each account's balance after it. Walking an account's entries in order, each one must start where
    /// the one before ended (an account starts at zero): an edited or deleted entry breaks the chain, even when the balances
    /// were moved to match.
    /// </summary>
    private static Task RunningBalanceProblemsAsync(MySqlConnection conn, MySqlTransaction tx, List<string> problems, CancellationToken ct) =>
        ReadLinesAsync(conn, tx, problems, $"""
            WITH legs AS (
              SELECT id, from_id AS account, -amount AS delta, from_after AS ended FROM entries
              UNION ALL
              SELECT id, to_id, amount, to_after FROM entries),
            chain AS (
              SELECT id, account, delta, ended, COALESCE(LAG(ended) OVER (PARTITION BY account ORDER BY id), 0) AS started FROM legs)
            SELECT id, account FROM chain WHERE started + delta <> ended ORDER BY id, account LIMIT {ProblemsPerCheck}
            """, r => $"Entry {r.GetInt64(0)} breaks the running balance of account {r.GetInt64(1)}.", ct);

    /// <summary>Each entry type moves coins only between the accounts its rule names, and carries what the rule requires.</summary>
    private static async Task EntryTypeProblemsAsync(MySqlConnection conn, MySqlTransaction tx, List<string> problems, CancellationToken ct)
    {
        await ReadLinesAsync(conn, tx, problems, $"""
            SELECT id FROM entries WHERE amount <= 0 OR from_id = to_id ORDER BY id LIMIT {ProblemsPerCheck}
            """, r => $"Entry {r.GetInt64(0)} moves nothing, or moves coins to the account they came from.", ct);

        // One row per combination, so the check stays small however long the ledger grows.
        await using var cmd = new MySqlCommand("""
            SELECT e.type, f.kind, t.kind, e.staff IS NOT NULL AND TRIM(e.staff) <> '' AS staffed,
                   e.hold_id IS NOT NULL AS held, e.reverses IS NOT NULL AS reversing, MIN(e.id), COUNT(*)
            FROM entries e LEFT JOIN accounts f ON f.id = e.from_id LEFT JOIN accounts t ON t.id = e.to_id
            GROUP BY e.type, f.kind, t.kind, staffed, held, reversing
            ORDER BY MIN(e.id)
            """, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var type = r.GetString(0);
            string? from = r.IsDBNull(1) ? null : r.GetString(1), to = r.IsDBNull(2) ? null : r.GetString(2);
            bool staffed = Flag(r, 3), held = Flag(r, 4), reversing = Flag(r, 5);
            var fits = EntryRules.TryGetValue(type, out var rule)
                && from is not null && rule.From.Contains(from) && to is not null && rule.To.Contains(to)
                && (staffed || !rule.Staff) && held == rule.Hold && reversing == rule.Reverses;
            if (fits) continue;
            var more = Convert.ToInt64(r.GetValue(7)) - 1;
            problems.Add($"Entry {r.GetInt64(6)} ({type}) doesn't fit its accounts" + (more > 0 ? $", nor do {more} more like it." : "."));
        }
    }

    /// <summary>A refund moves back exactly what one entry moved, and no entry is refunded twice.</summary>
    private static async Task RefundProblemsAsync(MySqlConnection conn, MySqlTransaction tx, List<string> problems, CancellationToken ct)
    {
        await ReadLinesAsync(conn, tx, problems, $"""
            SELECT r.id FROM entries r LEFT JOIN entries o ON o.id = r.reverses
            WHERE r.type = '{EntryType.Refund}'
              AND (o.id IS NULL OR o.id >= r.id OR o.from_id <> r.to_id OR o.to_id <> r.from_id OR o.amount <> r.amount
                   OR o.type IN ('{EntryType.Hold}', '{EntryType.HoldCompleted}', '{EntryType.HoldReturned}', '{EntryType.LateSale}', '{EntryType.FeeRefund}'))
            ORDER BY r.id LIMIT {ProblemsPerCheck}
            """, r => $"Refund {r.GetInt64(0)} doesn't reverse its entry.", ct);
        // A fee refund reverses what paid a fee out of a hold, to the hold's buyer, for the same amount.
        await ReadLinesAsync(conn, tx, problems, $"""
            SELECT r.id FROM entries r LEFT JOIN entries o ON o.id = r.reverses LEFT JOIN holds h ON h.id = o.hold_id
            WHERE r.type = '{EntryType.FeeRefund}'
              AND (o.id IS NULL OR o.id >= r.id
                   OR o.type NOT IN ('{EntryType.HoldCompleted}', '{EntryType.LateSale}') OR o.to_id <> r.from_id OR o.amount <> r.amount
                   OR h.id IS NULL OR h.buyer_id <> r.to_id OR h.kind NOT IN ('{EntryType.UploadFee}', '{EntryType.ClassifiedFee}', '{EntryType.GroupJoinFee}'))
            ORDER BY r.id LIMIT {ProblemsPerCheck}
            """, r => $"Fee refund {r.GetInt64(0)} doesn't reverse the payment of its fee.", ct);
        await ReadLinesAsync(conn, tx, problems, $"""
            SELECT reverses FROM entries WHERE reverses IS NOT NULL GROUP BY reverses HAVING COUNT(*) > 1 ORDER BY reverses LIMIT {ProblemsPerCheck}
            """, r => $"Entry {r.GetInt64(0)} was refunded more than once.", ct);
    }

    private static bool Flag(MySqlDataReader r, int i) => Convert.ToInt64(r.GetValue(i)) != 0;

    private static async Task ReadLinesAsync(MySqlConnection conn, MySqlTransaction tx, List<string> lines, string sql, Func<MySqlDataReader, string> line, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            lines.Add(line(r));
    }
}
