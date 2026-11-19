using GridGate.Launcher.Core.Money;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The owner's refund of a fee that was taken through a hold (upload, classified, group join): it comes back from whoever was
/// paid and never from the Grid bank; and wallet merges, which take a visitor's wallet only.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerFeeRefundTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid V = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly StaffAction Staff = new("Owner Person", "it was charged twice");
    private static readonly AccountRef PayerA = AccountRef.Resident(A);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MoneySettings Fees() => new(Mode: MoneyMode.Play, UploadFee: 5, ClassifiedFee: 25, ScriptPayouts: true);

    private static Task<long> Of(MoneyTestDatabase db, Guid id, string home = "") =>
        db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{id}' AND home = '{home}'");

    private static Task<long> FeesBalance(MoneyTestDatabase db) => db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {MoneySchema.FeesId}");

    private static HoldRequest FeeHold(string kind, long amount, AccountRef? founder = null) =>
        new(Guid.NewGuid(), kind, PayerA, founder, amount, Region, null, null, "p1");

    /// <summary>A fee held and completed: the hold's id and its opening entry (the one the payer's history shows).</summary>
    private static async Task<(long Hold, long Opening)> Paid(MoneyLedger ledger, HoldRequest request)
    {
        var held = await ledger.HoldAsync(request, Ct);
        Assert.Equal(MoneyOutcome.Ok, held.Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), held.HoldId!.Value, Ct)).Outcome);
        return (held.HoldId!.Value, held.EntryId!.Value);
    }

    private static async Task AssertSound(MoneyLedger ledger)
    {
        var check = await ledger.SelfCheckAsync(Ct);
        Assert.True(check.Ok, string.Join(" ", check.Problems));
    }

    [Theory]
    [InlineData(EntryType.UploadFee, 10)]
    [InlineData(EntryType.ClassifiedFee, 25)]
    [InlineData(EntryType.GroupJoinFee, 7)]
    public async Task A_fee_paid_to_Fees_comes_back_from_Fees(string kind, long amount)
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (_, opening) = await Paid(ledger, FeeHold(kind, amount));
        Assert.Equal(100 - amount, await Of(db, A));

        var refund = await ledger.RefundHeldFeeAsync(PayerA, opening, Staff, Ct);

        Assert.Equal(MoneyOutcome.Ok, refund.Outcome);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal(0, await FeesBalance(db));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE type = '{EntryType.FeeRefund}' AND amount = {amount}"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task An_ordinary_refund_cannot_undo_the_owners_fee_refund()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (_, opening) = await Paid(ledger, FeeHold(EntryType.UploadFee, 10));
        Assert.Equal(MoneyOutcome.Ok, (await ledger.RefundHeldFeeAsync(PayerA, opening, Staff, Ct)).Outcome);
        var feeRefund = await db.Scalar($"SELECT id FROM `money`.`entries` WHERE type = '{EntryType.FeeRefund}'");

        var undo = await ledger.RefundAsync(feeRefund, Staff, Ct);

        Assert.Equal(MoneyOutcome.NotAllowed, undo.Outcome);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal(0, await FeesBalance(db));
        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE type = '{EntryType.Refund}'"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task The_self_check_flags_an_ordinary_refund_of_a_fee_refund()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (_, opening) = await Paid(ledger, FeeHold(EntryType.UploadFee, 10));
        Assert.Equal(MoneyOutcome.Ok, (await ledger.RefundHeldFeeAsync(PayerA, opening, Staff, Ct)).Outcome);
        var feeRefund = await db.Scalar($"SELECT id FROM `money`.`entries` WHERE type = '{EntryType.FeeRefund}'");
        // As if someone wrote it into the books by hand: moneyuser can add entries.
        await db.Sql($"""
            INSERT INTO `money`.`entries` (type, from_id, to_id, amount, from_after, to_after, at, reverses)
            SELECT '{EntryType.Refund}', to_id, from_id, amount, 0, 0, UTC_TIMESTAMP(6), id FROM `money`.`entries` WHERE id = {feeRefund}
            """);

        var check = await ledger.SelfCheckAsync(Ct);

        Assert.False(check.Ok);
        Assert.Contains(check.Problems, p => p.Contains("doesn't reverse its entry"));
    }

    [Fact]
    public async Task A_group_join_fee_paid_to_a_founder_comes_back_from_the_founder()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (_, opening) = await Paid(ledger, FeeHold(EntryType.GroupJoinFee, 30, AccountRef.Resident(B)));
        Assert.Equal(130, await Of(db, B));

        var refund = await ledger.RefundHeldFeeAsync(PayerA, opening, Staff, Ct);

        Assert.Equal(MoneyOutcome.Ok, refund.Outcome);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal(100, await Of(db, B));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_founder_without_the_coins_refuses_the_refund_and_nothing_moves()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (_, opening) = await Paid(ledger, FeeHold(EntryType.GroupJoinFee, 30, AccountRef.Resident(B)));
        await ledger.TransferAsync(new TransferRequest(Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(B), PayerA, 130), Ct);
        Assert.Equal(0, await Of(db, B));

        var refund = await ledger.RefundHeldFeeAsync(PayerA, opening, Staff, Ct);

        Assert.Equal(MoneyOutcome.NotEnough, refund.Outcome);
        Assert.Equal("founder", refund.PayeeKind);
        Assert.Equal(0, await Of(db, B));
        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE type = '{EntryType.FeeRefund}'"));
    }

    [Fact]
    public async Task Fees_without_the_coins_refuses_the_refund()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (_, opening) = await Paid(ledger, FeeHold(EntryType.UploadFee, 10));
        Assert.Equal(MoneyOutcome.Ok, (await ledger.MoveFeesAsync(AccountRef.Resident(B), 10, Staff, Ct)).Outcome);

        var refund = await ledger.RefundHeldFeeAsync(PayerA, opening, Staff, Ct);

        Assert.Equal(MoneyOutcome.NotEnough, refund.Outcome);
        Assert.Equal("Fees", refund.PayeeKind);
        Assert.Equal(90, await Of(db, A));
    }

    [Fact]
    public async Task Each_fee_is_refunded_once_whichever_entry_of_it_is_named()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (hold, opening) = await Paid(ledger, FeeHold(EntryType.UploadFee, 10));
        var completing = await db.Scalar($"SELECT id FROM `money`.`entries` WHERE hold_id = {hold} AND type = '{EntryType.HoldCompleted}'");
        Assert.Equal(MoneyOutcome.Ok, (await ledger.RefundHeldFeeAsync(PayerA, opening, Staff, Ct)).Outcome);

        Assert.Equal(MoneyOutcome.AlreadyRefunded, (await ledger.RefundHeldFeeAsync(PayerA, opening, Staff, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.AlreadyRefunded, (await ledger.RefundHeldFeeAsync(PayerA, completing, Staff, Ct)).Outcome);

        Assert.Equal(100, await Of(db, A));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_fee_still_held_or_returned_cant_be_refunded()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var open = await ledger.HoldAsync(FeeHold(EntryType.UploadFee, 10), Ct);
        var returned = await ledger.HoldAsync(FeeHold(EntryType.UploadFee, 5), Ct);
        await ledger.ReturnHoldAsync(Guid.NewGuid(), returned.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.RefundHeldFeeAsync(PayerA, open.EntryId!.Value, Staff, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.RefundHeldFeeAsync(PayerA, returned.EntryId!.Value, Staff, Ct)).Outcome);
        Assert.Equal(90, await Of(db, A));
    }

    [Fact]
    public async Task A_sale_or_a_plain_charge_is_not_a_held_fee()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var sale = await ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), "object", PayerA, AccountRef.Resident(B), 20, Region, Guid.NewGuid(), "dress", "p1"), Ct);
        await ledger.CompleteHoldAsync(Guid.NewGuid(), sale.HoldId!.Value, Ct);
        var charge = await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.UploadFee, PayerA, 5, null), Ct);

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.RefundHeldFeeAsync(PayerA, sale.EntryId!.Value, Staff, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.RefundHeldFeeAsync(PayerA, charge.EntryId!.Value, Staff, Ct)).Outcome);
        Assert.Equal(75, await Of(db, A));
        Assert.Equal(MoneyOutcome.UnknownEntry, (await ledger.RefundHeldFeeAsync(PayerA, 99999, Staff, Ct)).Outcome);
    }

    [Fact]
    public async Task Only_the_person_who_paid_can_be_refunded_through_their_page()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (_, opening) = await Paid(ledger, FeeHold(EntryType.UploadFee, 10));
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.RefundHeldFeeAsync(AccountRef.Resident(B), opening, Staff, Ct)).Outcome);

        Assert.Equal(90, await Of(db, A));
        Assert.Equal(100, await Of(db, B));
    }

    [Fact]
    public async Task A_fee_refund_needs_a_reason()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (_, opening) = await Paid(ledger, FeeHold(EntryType.UploadFee, 10));

        Assert.Equal(MoneyOutcome.BadAmount, (await ledger.RefundHeldFeeAsync(PayerA, opening, new StaffAction("Owner Person", " "), Ct)).Outcome);
        Assert.Equal(90, await Of(db, A));
    }

    [Fact]
    public async Task A_fee_taken_late_after_its_hold_ran_out_can_be_refunded_too()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var hold = await ledger.HoldAsync(FeeHold(EntryType.UploadFee, 10), Ct);
        time.Advance(MoneyLedger.HoldLifetime + TimeSpan.FromSeconds(1));
        await ledger.ReturnExpiredHoldsAsync(Ct);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)).Outcome);
        Assert.Equal(90, await Of(db, A));

        Assert.Equal(MoneyOutcome.Ok, (await ledger.RefundHeldFeeAsync(PayerA, hold.EntryId!.Value, Staff, Ct)).Outcome);

        Assert.Equal(100, await Of(db, A));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task The_ordinary_refund_still_refuses_a_hold_entry()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var (hold, opening) = await Paid(ledger, FeeHold(EntryType.UploadFee, 10));
        var completing = await db.Scalar($"SELECT id FROM `money`.`entries` WHERE hold_id = {hold} AND type = '{EntryType.HoldCompleted}'");

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.RefundAsync(opening, Staff, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.RefundAsync(completing, Staff, Ct)).Outcome);
        Assert.Equal(90, await Of(db, A));
    }

    // --- Wallet merges ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resident_cant_be_merged_into_another_resident()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);

        var merge = await ledger.MergeWalletsAsync(AccountRef.Resident(A), AccountRef.Resident(B), Staff, Ct);

        Assert.Equal(MoneyOutcome.NotAVisitor, merge.Outcome);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal(100, await Of(db, B));
        Assert.Equal(0, await db.Scalar($"SELECT frozen FROM `money`.`accounts` WHERE agent_id = '{A}' AND home = ''"));
    }

    [Fact]
    public async Task A_resident_cant_be_merged_into_a_visitor_home_or_a_visitor_into_a_resident()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        var visitor = new AccountRef(V, "http://old.example:8002");
        await ledger.GiveAsync(visitor, 25, Staff, Ct);

        Assert.Equal(MoneyOutcome.NotAVisitor, (await ledger.MergeWalletsAsync(AccountRef.Resident(A), new AccountRef(A, "http://new.example:8002"), Staff, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NotAVisitor, (await ledger.MergeWalletsAsync(visitor, AccountRef.Resident(V), Staff, Ct)).Outcome);

        Assert.Equal(100, await Of(db, A));
        Assert.Equal(25, await Of(db, V, "http://old.example:8002"));
    }
}
