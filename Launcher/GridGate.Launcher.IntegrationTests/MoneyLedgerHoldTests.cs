using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>Purchase holds (money plan 1, task 4).</summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerHoldTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HoldRequest Buy(long amount, string kind = "object") =>
        new(Guid.NewGuid(), kind, AccountRef.Resident(A), AccountRef.Resident(B), amount, Region, Guid.NewGuid(), "dress");

    private static Task<long> Balance(MoneyTestDatabase db, long id) => db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {id}");

    private static Task<long> Of(MoneyTestDatabase db, Guid id) => db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{id}'");

    [Fact]
    public async Task Complete_pays_the_seller()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        var hold = await ledger.HoldAsync(Buy(40), Ct);
        Assert.Equal(MoneyOutcome.Ok, hold.Outcome);
        Assert.NotNull(hold.HoldId);
        Assert.Equal(60, await Of(db, A));
        Assert.Equal(40, await Balance(db, MoneySchema.HoldingId));

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Equal(0, await Balance(db, MoneySchema.HoldingId));
        Assert.Equal(140, await Of(db, B));
        Assert.Equal("completed", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}"));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'hold_completed' AND hold_id = {hold.HoldId}"));
    }

    [Fact]
    public async Task Return_gives_the_buyer_back()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(40), Ct);

        var back = await ledger.ReturnHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, back.Outcome);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal("returned", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}"));
    }

    [Fact]
    public async Task Settling_works_while_frozen()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(40), Ct);
        await db.Sql("UPDATE `money`.`state` SET value = '1' WHERE name = 'frozen'; UPDATE `money`.`accounts` SET frozen = 1;");

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Equal(140, await Of(db, B));
    }

    [Fact]
    public async Task Second_settle_is_HoldClosed()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(40), Ct);
        await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        var again = await ledger.ReturnHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.HoldClosed, again.Outcome);
        Assert.Equal(60, await Of(db, A));
        Assert.Equal(140, await Of(db, B));
    }

    [Fact]
    public async Task Unknown_hold_is_UnknownHold()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.UnknownHold, (await ledger.CompleteHoldAsync(Guid.NewGuid(), 999, Ct)).Outcome);
    }

    [Fact]
    public async Task Expired_holds_return_after_five_minutes()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.HoldAsync(Buy(40), Ct);

        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, await ledger.ReturnExpiredHoldsAsync(Ct));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await ledger.ReturnExpiredHoldsAsync(Ct));

        Assert.Equal(100, await Of(db, A));
    }

    [Fact]
    public async Task Hold_survives_a_new_ledger_instance_and_expires()
    {
        var (first, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await first.HoldAsync(Buy(40), Ct);
        var s = new MoneySettings(Mode: MoneyMode.Play);
        var second = new MoneyLedger(db.OpenMoney, () => s, () => MoneyMode.Play, MoneyLedgerTransferTests.LocalUsers(db), MoneyLedgerTransferTests.KnownVisitors(db), time);

        time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(1, await second.ReturnExpiredHoldsAsync(Ct));
        Assert.Equal(100, await Of(db, A));
    }

    [Fact]
    public async Task Hold_needs_enough_coins()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.NotEnough, (await ledger.HoldAsync(Buy(101), Ct)).Outcome);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds`"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hold_whose_amount_was_changed_isnt_settled_and_freezes_payments(bool complete)
    {
        // Money review 2026-11-15, L-3: settling paid holds.amount without a guard, so one changed value made coins.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(10), Ct);
        await db.Sql($"UPDATE `money`.`holds` SET amount = 1000 WHERE id = {hold.HoldId}");

        var settled = complete
            ? await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)
            : await ledger.ReturnHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.HoldMismatch, settled.Outcome);
        Assert.Equal(90, await Of(db, A));
        Assert.Equal(100, await Of(db, B));
        Assert.Equal(10, await Balance(db, MoneySchema.HoldingId));
        Assert.Equal("open", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}"));
        var frozen = await ledger.FrozenAsync(Ct);
        Assert.True(frozen.Frozen);
        Assert.Contains("hold", frozen.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_mismatched_hold_keeps_an_earlier_freeze_reason()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(10), Ct);
        await ledger.SetFrozenAsync(true, "Owner's own reason", Ct);
        await db.Sql($"UPDATE `money`.`holds` SET amount = 1000 WHERE id = {hold.HoldId}");

        await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal("Owner's own reason", (await ledger.FrozenAsync(Ct)).Reason);
    }

    [Fact]
    public async Task A_hold_that_would_take_holding_below_zero_isnt_settled()
    {
        // The hold and its entry agree, but Holding's own balance was lowered.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(10), Ct);
        await db.Sql($"UPDATE `money`.`accounts` SET balance = 5 WHERE id = {MoneySchema.HoldingId}");

        var settled = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.HoldMismatch, settled.Outcome);
        Assert.Equal(100, await Of(db, B));
    }

    [Fact]
    public async Task Hold_region_is_remembered()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(10), Ct);

        Assert.Equal(Region, await ledger.HoldRegionAsync(hold.HoldId!.Value, Ct));
        Assert.Null(await ledger.HoldRegionAsync(12345, Ct));
    }

    // --- Fee holds: an upload, classified or group join fee held before its action ----------------------------------

    private static readonly Guid NoWallet = Guid.Parse("d0000000-0000-0000-0000-00000000000d");

    private static HoldRequest Fee(string kind, long amount, AccountRef? payee = null) =>
        new(Guid.NewGuid(), kind, AccountRef.Resident(A), payee, amount, Region, null, null, "p1");

    private static async Task AssertSound(MoneyLedger ledger)
    {
        var check = await ledger.SelfCheckAsync(Ct);
        Assert.True(check.Ok, string.Join(" ", check.Problems));
    }

    [Fact]
    public async Task An_upload_fee_hold_completes_into_fees()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var fees = await Balance(db, MoneySchema.FeesId);

        var h = await ledger.HoldAsync(Fee(EntryType.UploadFee, 10), Ct);
        Assert.Equal(MoneyOutcome.Ok, h.Outcome);
        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), h.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Null(done.ToBalance);
        Assert.Equal(90, await Of(db, A));
        Assert.Equal(fees + 10, await Balance(db, MoneySchema.FeesId));
        Assert.Equal(0, await Balance(db, MoneySchema.HoldingId));
        Assert.Equal(2, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE hold_id = {h.HoldId} AND reason = 'upload_fee'"));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE hold_id = {h.HoldId} AND type = 'hold'"));
        Assert.Equal(1, await db.Scalar(
            $"SELECT COUNT(*) FROM `money`.`entries` WHERE hold_id = {h.HoldId} AND type = 'hold_completed' AND to_id = {MoneySchema.FeesId}"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_classified_fee_hold_completes_into_fees()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var fees = await Balance(db, MoneySchema.FeesId);

        var h = await ledger.HoldAsync(Fee(EntryType.ClassifiedFee, 25), Ct);
        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), h.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Equal(75, await Of(db, A));
        Assert.Equal(fees + 25, await Balance(db, MoneySchema.FeesId));
        Assert.Equal(2, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE hold_id = {h.HoldId} AND reason = 'classified_fee'"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_group_join_fee_hold_completes_to_the_founder()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var fees = await Balance(db, MoneySchema.FeesId);

        var h = await ledger.HoldAsync(Fee(EntryType.GroupJoinFee, 20, AccountRef.Resident(B)), Ct);
        Assert.Equal(MoneyOutcome.Ok, h.Outcome);
        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), h.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Equal(120, done.ToBalance);
        Assert.Equal(80, await Of(db, A));
        Assert.Equal(120, await Of(db, B));
        Assert.Equal(fees, await Balance(db, MoneySchema.FeesId));
        Assert.Equal(2, await db.Scalar($"SELECT COUNT(*) FROM `money`.`entries` WHERE hold_id = {h.HoldId} AND reason = 'group_join_fee'"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_group_join_fee_hold_for_a_founder_without_a_wallet_goes_to_fees()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var fees = await Balance(db, MoneySchema.FeesId);

        var h = await ledger.HoldAsync(Fee(EntryType.GroupJoinFee, 20, AccountRef.Resident(NoWallet)), Ct);
        Assert.Equal(MoneyOutcome.Ok, h.Outcome);
        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), h.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Null(done.ToBalance);
        Assert.Equal(fees + 20, await Balance(db, MoneySchema.FeesId));
        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE agent_id = '{NoWallet}'"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_group_join_fee_hold_without_a_founder_goes_to_fees()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var fees = await Balance(db, MoneySchema.FeesId);

        var h = await ledger.HoldAsync(Fee(EntryType.GroupJoinFee, 20), Ct);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), h.HoldId!.Value, Ct)).Outcome);

        Assert.Equal(fees + 20, await Balance(db, MoneySchema.FeesId));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_returned_fee_hold_gives_the_coins_back()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var fees = await Balance(db, MoneySchema.FeesId);
        var h = await ledger.HoldAsync(Fee(EntryType.GroupJoinFee, 20, AccountRef.Resident(B)), Ct);

        var back = await ledger.ReturnHoldAsync(Guid.NewGuid(), h.HoldId!.Value, Ct);

        Assert.Equal(MoneyOutcome.Ok, back.Outcome);
        Assert.Equal(100, back.ToBalance);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal(100, await Of(db, B));
        Assert.Equal(fees, await Balance(db, MoneySchema.FeesId));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task An_expired_fee_hold_returns_to_the_payer()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.HoldAsync(Fee(EntryType.UploadFee, 10), Ct);

        time.Advance(MoneyLedger.HoldLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(1, await ledger.ReturnExpiredHoldsAsync(Ct));
        Assert.Equal(100, await Of(db, A));
        await AssertSound(ledger);
    }

    [Theory]
    [InlineData(EntryType.UploadFee)]
    [InlineData(EntryType.ClassifiedFee)]
    public async Task An_upload_fee_hold_naming_a_payee_is_not_allowed(string kind)
    {
        // Upload and classified fees are the grid's: a region can't name a seller to pay them to.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.HoldAsync(Fee(kind, 10, AccountRef.Resident(B)), Ct)).Outcome);

        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds`"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_group_join_fee_hold_naming_the_buyer_as_founder_pays_fees_as_a_charge_would()
    {
        // A charge sends a join fee whose founder is the payer to Fees; the hold that replaces it does the same.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var fees = await Balance(db, MoneySchema.FeesId);

        var h = await ledger.HoldAsync(Fee(EntryType.GroupJoinFee, 10, AccountRef.Resident(A)), Ct);
        Assert.Equal(MoneyOutcome.Ok, h.Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), h.HoldId!.Value, Ct)).Outcome);

        Assert.Equal(90, await Of(db, A));
        Assert.Equal(fees + 10, await Balance(db, MoneySchema.FeesId));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_visitors_group_join_fee_hold_naming_a_resident_with_their_id_pays_fees_as_a_charge_would()
    {
        // A charge compares the founder with the payer by id alone, whatever the payer's home: so does the hold, so a
        // visitor whose id is also a resident's can't pay that resident through a join fee.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await db.AddUser(MoneyLedgerTransferTests.V);
        var visitor = new AccountRef(MoneyLedgerTransferTests.V, "http://other.example:8002");
        var resident = AccountRef.Resident(MoneyLedgerTransferTests.V);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.GiveAsync(visitor, 50, new StaffAction("Owner", "welcome"), Ct)).Outcome);
        var residentBefore = (await ledger.BalanceAsync(resident, Ct)).FromBalance;
        var fees = await Balance(db, MoneySchema.FeesId);

        var h = await ledger.HoldAsync(new HoldRequest(Guid.NewGuid(), EntryType.GroupJoinFee, visitor, resident, 10, Region, null, null, "p1"), Ct);
        Assert.Equal(MoneyOutcome.Ok, h.Outcome);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), h.HoldId!.Value, Ct)).Outcome);

        Assert.Equal(residentBefore, (await ledger.BalanceAsync(resident, Ct)).FromBalance);
        Assert.Equal(fees + 10, await Balance(db, MoneySchema.FeesId));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_group_join_fee_hold_to_a_visitor_is_not_allowed()
    {
        // A founder is a resident of this grid: a region can't route a join fee to a visitor's wallet.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var visitor = new AccountRef(MoneyLedgerTransferTests.V, "http://other.example:8002");
        Assert.Equal(MoneyOutcome.Ok, (await ledger.GiveAsync(visitor, 1, new StaffAction("Owner", "welcome"), Ct)).Outcome);

        Assert.Equal(MoneyOutcome.NotAllowed, (await ledger.HoldAsync(Fee(EntryType.GroupJoinFee, 10, visitor), Ct)).Outcome);

        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds`"));
        await AssertSound(ledger);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("land")]
    public async Task An_object_hold_without_a_seller_is_refused(string kind)
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.NotEqual(MoneyOutcome.Ok, (await ledger.HoldAsync(Fee(kind, 10), Ct)).Outcome);

        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds`"));
    }

    [Fact]
    public async Task A_fee_hold_follows_the_payment_rules()
    {
        // As a charge would: no more than the largest payment, nothing while payments are frozen, none from a frozen wallet.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        Assert.Equal(MoneyOutcome.Ok, (await ledger.BalanceAsync(AccountRef.Resident(A), Ct)).Outcome); // A's wallet, with 100

        Assert.Equal(MoneyOutcome.BadAmount, (await ledger.HoldAsync(Fee(EntryType.UploadFee, 0), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.BadAmount, (await ledger.HoldAsync(Fee(EntryType.UploadFee, 1_000_001), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NotEnough, (await ledger.HoldAsync(Fee(EntryType.UploadFee, 101), Ct)).Outcome);
        await ledger.SetFrozenAsync(true, "test", Ct);
        Assert.Equal(MoneyOutcome.Frozen, (await ledger.HoldAsync(Fee(EntryType.UploadFee, 10), Ct)).Outcome);
        await ledger.SetFrozenAsync(false, "test", Ct);
        await db.Sql($"UPDATE `money`.`accounts` SET frozen = 1 WHERE agent_id = '{A}'");
        Assert.Equal(MoneyOutcome.AccountFrozen, (await ledger.HoldAsync(Fee(EntryType.UploadFee, 10), Ct)).Outcome);

        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds`"));
    }
}
