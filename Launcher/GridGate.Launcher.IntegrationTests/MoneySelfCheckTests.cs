using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// What the self-check catches beyond "balances sum to zero and match their entries" (money review 2026-11-15, L-3): a
/// negative account, Holding against its open holds, a hold against its entries, a broken running balance (an edited or
/// deleted entry), an entry type on the wrong accounts, and a system account's kind. Tampering is done as root with the
/// "entries never change" triggers dropped, as an attacker with root could.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneySelfCheckTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly StaffAction Staff = new("Owner Person", "event prize");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TransferRequest Pay(Guid from, Guid to, long amount) =>
        new(Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(from), AccountRef.Resident(to), amount);

    private static HoldRequest Buy(long amount) =>
        new(Guid.NewGuid(), "object", AccountRef.Resident(A), AccountRef.Resident(B), amount, Region, Guid.NewGuid(), "dress");

    private static HoldRequest FeeHold(string kind, AccountRef? payee) =>
        new(Guid.NewGuid(), kind, AccountRef.Resident(A), payee, 3, Region, null, null);

    [Theory]
    [InlineData(EntryType.UploadFee)]
    [InlineData(EntryType.ClassifiedFee)]
    public async Task A_grid_fee_hold_rewritten_to_pay_a_wallet_is_found(string kind)
    {
        // Upload and classified fees are the grid's: a completed one that paid anyone but Fees was changed.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.TransferAsync(Pay(B, A, 1), Ct); // B has a wallet
        var hold = await ledger.HoldAsync(FeeHold(kind, null), Ct);
        await db.Sql($"UPDATE `money`.`holds` SET seller_id = {await IdOf(db, B)} WHERE id = {hold.HoldId}");
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)).Outcome);

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Hold {hold.HoldId} doesn't match its entries");
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("visitor")]
    public async Task A_group_join_fee_hold_rewritten_to_pay_the_buyer_or_a_visitor_is_found(string payee)
    {
        // A join fee pays Fees or a resident founder other than the buyer: a completed one that paid anyone else was changed.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var visitor = new AccountRef(MoneyLedgerTransferTests.V, "http://other.example:8002");
        Assert.Equal(MoneyOutcome.Ok, (await ledger.GiveAsync(visitor, 1, Staff, Ct)).Outcome);
        var hold = await ledger.HoldAsync(FeeHold(EntryType.GroupJoinFee, null), Ct);
        var seller = payee == "buyer" ? await IdOf(db, A) : await IdOf(db, MoneyLedgerTransferTests.V);
        await db.Sql($"UPDATE `money`.`holds` SET seller_id = {seller} WHERE id = {hold.HoldId}");
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)).Outcome);

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Hold {hold.HoldId} doesn't match its entries");
    }

    [Fact]
    public async Task A_purchase_hold_rewritten_to_pay_fees_is_found()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(10), Ct);
        await db.Sql($"UPDATE `money`.`holds` SET seller_id = {MoneySchema.FeesId} WHERE id = {hold.HoldId}");
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)).Outcome);

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Hold {hold.HoldId} doesn't match its entries");
    }

    private static Task<long> IdOf(MoneyTestDatabase db, Guid agent) => db.Scalar($"SELECT id FROM `money`.`accounts` WHERE agent_id = '{agent}'");

    private static Task AllowEntryTampering(MoneyTestDatabase db) =>
        db.Sql("DROP TRIGGER IF EXISTS `money`.`entries_never_edited`; DROP TRIGGER IF EXISTS `money`.`entries_never_deleted`;");

    private static void AssertFound(SelfCheckResult r, string words)
    {
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains(words, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Books_built_by_every_kind_of_movement_pass()
    {
        var (ledger, _, time) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, WeeklyAllowance: 5));
        await ledger.TransferAsync(Pay(A, B, 30), Ct);
        var sold = await ledger.HoldAsync(Buy(10), Ct);
        await ledger.CompleteHoldAsync(Guid.NewGuid(), sold.HoldId!.Value, Ct);
        var back = await ledger.HoldAsync(Buy(7), Ct);
        await ledger.ReturnHoldAsync(Guid.NewGuid(), back.HoldId!.Value, Ct);
        await ledger.HoldAsync(Buy(4), Ct); // still open
        await ledger.GiveAsync(AccountRef.Resident(A), 50, Staff, Ct);
        await ledger.TakeAsync(AccountRef.Resident(B), 20, Staff, Ct);
        var paid = await ledger.TransferAsync(Pay(B, A, 3), Ct);
        await ledger.RefundAsync(paid.EntryId!.Value, Staff, Ct);
        await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.UploadFee, AccountRef.Resident(A), 10, null), Ct);
        await ledger.MoveFeesAsync(AccountRef.Resident(B), 5, Staff, Ct);
        // Fees held before their action: one into Fees, one to a group's founder, one given back.
        var upload = await ledger.HoldAsync(FeeHold(EntryType.UploadFee, null), Ct);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), upload.HoldId!.Value, Ct)).Outcome);
        var join = await ledger.HoldAsync(FeeHold(EntryType.GroupJoinFee, AccountRef.Resident(B)), Ct);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), join.HoldId!.Value, Ct)).Outcome);
        var ad = await ledger.HoldAsync(FeeHold(EntryType.ClassifiedFee, null), Ct);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.ReturnHoldAsync(Guid.NewGuid(), ad.HoldId!.Value, Ct)).Outcome);
        time.Advance(TimeSpan.FromDays(8));
        await ledger.TransferAsync(Pay(A, B, 1), Ct); // both get an allowance first

        var r = await ledger.SelfCheckAsync(Ct);

        Assert.Empty(r.Problems);
        Assert.Empty(r.Mismatches);
        Assert.True(r.Ok);
    }

    [Fact]
    public async Task A_negative_wallet_is_found()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.TransferAsync(Pay(A, B, 30), Ct);
        var a = await IdOf(db, A);
        var b = await IdOf(db, B);
        // Moved between two wallets, so the sum still balances; the entries no longer match either.
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance - 100 WHERE id = {a}; UPDATE `money`.`accounts` SET balance = balance + 100 WHERE id = {b};");

        var r = await ledger.SelfCheckAsync(Ct);

        AssertFound(r, $"Account {a} is below zero");
        Assert.True((await ledger.FrozenAsync(Ct)).Frozen);
    }

    [Fact]
    public async Task A_hold_whose_amount_was_changed_is_found()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(10), Ct);
        await db.Sql($"UPDATE `money`.`holds` SET amount = 1000 WHERE id = {hold.HoldId}");

        var r = await ledger.SelfCheckAsync(Ct);

        AssertFound(r, "Holding has 10 but its open holds add up to 1000");
        AssertFound(r, $"Hold {hold.HoldId} doesn't match its entries");
    }

    [Fact]
    public async Task A_settled_hold_whose_amount_was_changed_is_found()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(10), Ct);
        await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);
        await db.Sql($"UPDATE `money`.`holds` SET amount = 1000 WHERE id = {hold.HoldId}");

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Hold {hold.HoldId} doesn't match its entries");
    }

    [Fact]
    public async Task A_rewritten_entry_is_found()
    {
        // The review's case: pay 30, rewrite the entry to 5 and move both balances back by 25. Balances still match the entries.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var paid = await ledger.TransferAsync(Pay(A, B, 30), Ct);
        await AllowEntryTampering(db);
        await db.Sql($"UPDATE `money`.`entries` SET amount = 5 WHERE id = {paid.EntryId}");
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance + 25 WHERE id = {await IdOf(db, A)}");
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance - 25 WHERE id = {await IdOf(db, B)}");

        var r = await ledger.SelfCheckAsync(Ct);

        Assert.Empty(r.Mismatches);
        AssertFound(r, $"Entry {paid.EntryId} breaks the running balance");
    }

    [Fact]
    public async Task A_deleted_entry_is_found()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var first = await ledger.TransferAsync(Pay(A, B, 30), Ct);
        var second = await ledger.TransferAsync(Pay(A, B, 10), Ct);
        await AllowEntryTampering(db);
        await db.Sql($"DELETE FROM `money`.`entries` WHERE id = {first.EntryId}");
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance + 30 WHERE id = {await IdOf(db, A)}");
        await db.Sql($"UPDATE `money`.`accounts` SET balance = balance - 30 WHERE id = {await IdOf(db, B)}");

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Entry {second.EntryId} breaks the running balance");
    }

    [Fact]
    public async Task An_entry_type_on_the_wrong_accounts_is_found()
    {
        // A payment between two residents relabelled as a starting balance, which only ever comes from the Grid bank.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var paid = await ledger.TransferAsync(Pay(A, B, 30), Ct);
        await AllowEntryTampering(db);
        await db.Sql($"UPDATE `money`.`entries` SET type = 'starting_balance' WHERE id = {paid.EntryId}");

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Entry {paid.EntryId} (starting_balance) doesn't fit its accounts");
    }

    [Fact]
    public async Task A_staff_entry_without_a_staff_name_is_found()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var gift = await ledger.GiveAsync(AccountRef.Resident(A), 50, Staff, Ct);
        await AllowEntryTampering(db);
        await db.Sql($"UPDATE `money`.`entries` SET staff = NULL WHERE id = {gift.EntryId}");

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Entry {gift.EntryId} (staff_gift) doesn't fit its accounts");
    }

    [Fact]
    public async Task An_unknown_entry_type_is_found()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var paid = await ledger.TransferAsync(Pay(A, B, 30), Ct);
        await AllowEntryTampering(db);
        await db.Sql($"UPDATE `money`.`entries` SET type = 'object_sale' WHERE id = {paid.EntryId}");

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Entry {paid.EntryId} (object_sale) doesn't fit its accounts");
    }

    [Fact]
    public async Task A_refund_that_doesnt_reverse_its_entry_is_found()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var paid = await ledger.TransferAsync(Pay(A, B, 30), Ct);
        var refund = await ledger.RefundAsync(paid.EntryId!.Value, Staff, Ct);
        await AllowEntryTampering(db);
        // Pointed away from the payment, so the payment could be refunded a second time.
        await db.Sql($"UPDATE `money`.`entries` SET reverses = {refund.EntryId} WHERE id = {refund.EntryId}");

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Refund {refund.EntryId} doesn't reverse its entry");
    }

    [Fact]
    public async Task A_wallet_turned_into_a_bank_is_found()
    {
        // Only the Grid bank may go below zero, so a second "bank" could spend without limit.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        var a = await IdOf(db, A);
        await db.Sql($"UPDATE `money`.`accounts` SET kind = 'Bank' WHERE id = {a}");

        AssertFound(await ledger.SelfCheckAsync(Ct), $"Account {a} has the wrong kind");
    }
}
