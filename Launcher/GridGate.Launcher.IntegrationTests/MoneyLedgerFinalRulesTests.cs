using GridGate.Launcher.Core.Money;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// Fee amounts that must match the configured fees, a sale completed after its hold ran out, answers kept per computer
/// rather than per key, the old stored answers dropped, and the cap on one key's script payouts.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerFinalRulesTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MoneySettings Fees(long upload = 5, long classified = 25, long dailyPayout = 5000, long keyPayout = 20_000) =>
        new(Mode: MoneyMode.Play, UploadFee: upload, ClassifiedFee: classified, DailyPayoutLimit: dailyPayout, DailyKeyPayoutLimit: keyPayout,
            ScriptPayouts: true);

    private static Task<long> Of(MoneyTestDatabase db, Guid id) => db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{id}' AND home = ''");

    private static HoldRequest FeeHold(string kind, long amount) =>
        new(Guid.NewGuid(), kind, AccountRef.Resident(A), null, amount, Region, null, null, "p1");

    private static HoldRequest Buy(long amount, string kind = "object") =>
        new(Guid.NewGuid(), kind, AccountRef.Resident(A), AccountRef.Resident(B), amount, Region, Guid.NewGuid(), "dress", "p1");

    private static TransferRequest Pay(long amount) =>
        new(Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(A), AccountRef.Resident(B), amount);

    // --- Fee amounts ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(25, true)]
    [InlineData(24, false)]
    [InlineData(26, false)]
    [InlineData(50, false)]
    public async Task A_classified_fee_must_be_the_configured_fee(long amount, bool allowed)
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());

        var charge = await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.ClassifiedFee, AccountRef.Resident(A), amount, null), Ct);
        var hold = await ledger.HoldAsync(FeeHold(EntryType.ClassifiedFee, amount), Ct);

        Assert.Equal(allowed ? MoneyOutcome.Ok : MoneyOutcome.BadAmount, charge.Outcome);
        Assert.Equal(allowed ? MoneyOutcome.Ok : MoneyOutcome.BadAmount, hold.Outcome);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(15, true)]
    [InlineData(4, false)]
    [InlineData(12, false)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public async Task An_upload_fee_must_be_a_whole_multiple_of_the_configured_fee(long amount, bool allowed)
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);

        var charge = await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.UploadFee, AccountRef.Resident(A), amount, null), Ct);
        var hold = await ledger.HoldAsync(FeeHold(EntryType.UploadFee, amount), Ct);

        Assert.Equal(allowed ? MoneyOutcome.Ok : MoneyOutcome.BadAmount, charge.Outcome);
        Assert.Equal(allowed ? MoneyOutcome.Ok : MoneyOutcome.BadAmount, hold.Outcome);
        Assert.Equal(allowed ? 100 - 2 * amount : 100, await Of(db, A));
    }

    [Fact]
    public async Task Fees_configured_as_nothing_allow_no_upload_or_classified_charge()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria, Fees(upload: 0, classified: 0));

        Assert.Equal(MoneyOutcome.BadAmount, (await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.UploadFee, AccountRef.Resident(A), 5, null), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.BadAmount, (await ledger.HoldAsync(FeeHold(EntryType.ClassifiedFee, 25), Ct)).Outcome);
    }

    [Fact]
    public async Task A_group_join_fee_has_no_configured_amount_to_match()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());

        Assert.Equal(MoneyOutcome.Ok, (await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.GroupJoinFee, AccountRef.Resident(A), 7, null), Ct)).Outcome);
    }

    // --- A sale completed after its hold ran out -----------------------------------------------------------------------

    private static async Task<long> ExpiredHold(MoneyLedger ledger, FakeTimeProvider time, HoldRequest request)
    {
        var hold = await ledger.HoldAsync(request, Ct);
        Assert.Equal(MoneyOutcome.Ok, hold.Outcome);
        time.Advance(MoneyLedger.HoldLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await ledger.ReturnExpiredHoldsAsync(Ct));
        return hold.HoldId!.Value;
    }

    private static async Task AssertSound(MoneyLedger ledger)
    {
        var check = await ledger.SelfCheckAsync(Ct);
        Assert.True(check.Ok, string.Join(" ", check.Problems));
    }

    [Fact]
    public async Task A_complete_after_the_sweep_returned_the_hold_takes_the_coins_again_and_pays_the_seller()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var hold = await ExpiredHold(ledger, time, Buy(40));
        Assert.Equal(100, await Of(db, A));

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Equal(60, await Of(db, A));
        Assert.Equal(140, await Of(db, B));
        Assert.Equal(140, done.ToBalance);
        Assert.Equal(0, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {MoneySchema.HoldingId}"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_late_complete_takes_the_coins_once_however_often_it_is_asked()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var hold = await ExpiredHold(ledger, time, Buy(40));
        var txn = Guid.NewGuid();
        var first = await ledger.CompleteHoldAsync(txn, hold, Ct);

        // The same transaction id gets the first answer; a new one finds the hold closed. Neither takes more coins.
        Assert.Equal(first, await ledger.CompleteHoldAsync(txn, hold, Ct));
        Assert.Equal(MoneyOutcome.HoldClosed, (await ledger.CompleteHoldAsync(Guid.NewGuid(), hold, Ct)).Outcome);
        Assert.Equal(MoneyOutcome.HoldClosed, (await ledger.ReturnHoldAsync(Guid.NewGuid(), hold, Ct)).Outcome);

        Assert.Equal(60, await Of(db, A));
        Assert.Equal(140, await Of(db, B));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_late_complete_that_the_payer_cant_cover_stays_hold_closed_and_says_the_seller_wasnt_paid()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var hold = await ExpiredHold(ledger, time, Buy(40));
        await ledger.TransferAsync(Pay(90), Ct);

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold, Ct);

        Assert.Equal(MoneyOutcome.HoldClosed, done.Outcome);
        Assert.True(done.SaleUnpaid);
        Assert.Equal(10, await Of(db, A));
        Assert.Equal(190, await Of(db, B));
        Assert.Equal("returned", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold}"));
    }

    [Fact]
    public async Task A_late_complete_is_refused_while_payments_are_frozen_and_says_the_seller_wasnt_paid()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var hold = await ExpiredHold(ledger, time, Buy(40));
        await ledger.SetFrozenAsync(true, "test", Ct);

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold, Ct);

        Assert.Equal(MoneyOutcome.HoldClosed, done.Outcome);
        Assert.True(done.SaleUnpaid);
        Assert.Equal(100, await Of(db, A));
    }

    [Fact]
    public async Task A_late_complete_is_checked_against_the_payers_daily_limit_like_a_hold()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees() with { DailySpendLimit = 50 });
        var hold = await ExpiredHold(ledger, time, Buy(40));
        // The returned hold no longer counts, so this 30 fits the day's 50, but then 40 more doesn't.
        Assert.Equal(MoneyOutcome.Ok, (await ledger.TransferAsync(Pay(30), Ct)).Outcome);

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold, Ct);

        Assert.Equal(MoneyOutcome.HoldClosed, done.Outcome);
        Assert.True(done.SaleUnpaid);
        Assert.Equal(70, await Of(db, A));
    }

    [Fact]
    public async Task A_complete_after_a_return_on_request_stays_hold_closed_and_takes_nothing()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var hold = (await ledger.HoldAsync(Buy(40), Ct)).HoldId!.Value;
        Assert.Equal(MoneyOutcome.Ok, (await ledger.ReturnHoldAsync(Guid.NewGuid(), hold, Ct)).Outcome);

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold, Ct);

        Assert.Equal(MoneyOutcome.HoldClosed, done.Outcome);
        Assert.False(done.SaleUnpaid);
        Assert.Equal(100, await Of(db, A));
        Assert.Equal(100, await Of(db, B));
    }

    [Fact]
    public async Task A_late_complete_of_a_fee_hold_pays_fees_and_shows_no_balance()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var hold = await ExpiredHold(ledger, time, FeeHold(EntryType.UploadFee, 10));
        var fees = await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {MoneySchema.FeesId}");

        var done = await ledger.CompleteHoldAsync(Guid.NewGuid(), hold, Ct);

        Assert.Equal(MoneyOutcome.Ok, done.Outcome);
        Assert.Null(done.ToBalance);
        Assert.Equal(90, await Of(db, A));
        Assert.Equal(fees + 10, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {MoneySchema.FeesId}"));
        await AssertSound(ledger);
    }

    [Fact]
    public async Task A_late_complete_of_a_land_sale_pays_the_seller()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var hold = await ExpiredHold(ledger, time, Buy(30, "land"));

        Assert.Equal(MoneyOutcome.Ok, (await ledger.CompleteHoldAsync(Guid.NewGuid(), hold, Ct)).Outcome);

        Assert.Equal(130, await Of(db, B));
        Assert.Equal(70, await Of(db, A));
        await AssertSound(ledger);
    }

    // --- Answers kept per computer ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_repeat_from_another_key_of_the_same_computer_gets_the_stored_answer()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        var txn = Guid.NewGuid();
        TransferRequest PayVia(string key, long amount = 10) =>
            new(txn, EntryType.PayAvatar, AccountRef.Resident(A), AccountRef.Resident(B), amount, Region, KeyId: key);
        var first = await ledger.TransferAsync(PayVia("server:one"), Ct);

        var again = await ledger.TransferAsync(PayVia("server:two"), Ct);

        Assert.Equal(MoneyOutcome.Ok, first.Outcome);
        Assert.Equal(first, again);
        Assert.Equal(first, await ledger.EarlierAsync(PayVia("server:two"), Ct));
        Assert.Equal(90, await Of(db, A));
        // Other values under that id are still another request.
        Assert.Equal(MoneyOutcome.DuplicateTxn, (await ledger.TransferAsync(PayVia("server:two", 11), Ct)).Outcome);
    }

    [Fact]
    public async Task Stored_answers_older_than_thirty_days_are_dropped_and_newer_ones_kept()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees());
        await ledger.TransferAsync(Pay(1), Ct);
        time.Advance(TimeSpan.FromDays(29));
        await ledger.TransferAsync(Pay(2), Ct);
        time.Advance(TimeSpan.FromDays(2));

        var dropped = await ledger.PruneRequestsAsync(Ct);

        Assert.Equal(1, dropped);
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`requests`"));
        Assert.Equal(0, await ledger.PruneRequestsAsync(Ct));
    }

    // --- One key's script payouts, grid-wide ---------------------------------------------------------------------------

    private static PayoutRequest Payout(Guid owner, long amount, string key = "p1") =>
        new(Guid.NewGuid(), owner, AccountRef.Resident(B), amount, Region, Guid.NewGuid(), "slot machine", key);

    private static async Task<Guid> Owner(MoneyLedger ledger, MoneyTestDatabase db, string prefix, int i)
    {
        var owner = Guid.Parse($"{prefix}-0000-0000-0000-{i:D12}");
        await db.AddUser(owner);
        await ledger.SetPayoutsAllowedAsync(owner, true, Ct);
        return owner;
    }

    [Fact]
    public async Task One_keys_payouts_in_a_day_stop_at_fifty_times_the_resident_limit_whoever_the_owners_are()
    {
        // The key's own limit is far above the cap, and each owner may pay 20 a day: the cap is 50 x 20 = 1000.
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria, Fees(dailyPayout: 20, keyPayout: 1_000_000));
        for (var i = 0; i < 50; i++)
            Assert.Equal(MoneyOutcome.Ok, (await ledger.PayoutAsync(Payout(await Owner(ledger, db, "a1000000", i), 20), Ct)).Outcome);
        var next = await Owner(ledger, db, "a1000000", 50);

        Assert.Equal(MoneyOutcome.OverKeyLimit, (await ledger.PayoutAsync(Payout(next, 1), Ct)).Outcome);
        // Another key has its own cap, and the cap opens again a day later.
        Assert.Equal(MoneyOutcome.Ok, (await ledger.PayoutAsync(Payout(next, 1, "p2"), Ct)).Outcome);
        time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(MoneyOutcome.Ok, (await ledger.PayoutAsync(Payout(next, 1), Ct)).Outcome);
        Assert.Equal(1000 + 2, await db.Scalar("SELECT COALESCE(SUM(amount), 0) FROM `money`.`entries` WHERE type = 'script_payout'"));
    }

    [Fact]
    public async Task The_owners_a_key_paid_for_today_are_counted_once_each()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, Fees(dailyPayout: 20, keyPayout: 1_000_000));
        for (var i = 0; i < 3; i++)
        {
            var owner = await Owner(ledger, db, "a3000000", i);
            await ledger.PayoutAsync(Payout(owner, 5), Ct);
            await ledger.PayoutAsync(Payout(owner, 5), Ct);
        }

        Assert.Equal(3, await ledger.KeyPayoutOwnersAsync("p1", Ct));
        Assert.Equal(0, await ledger.KeyPayoutOwnersAsync("p2", Ct));
    }
}
