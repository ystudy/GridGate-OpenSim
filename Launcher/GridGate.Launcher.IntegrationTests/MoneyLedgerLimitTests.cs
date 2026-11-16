using System.Collections.Concurrent;
using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// Daily limits per person and per region key, with the owner's own limits (verified-regions design §8.7), on a real
/// MariaDB. Key p1 and p2 are region programs; <see cref="Server"/> is a paired Region Server's key id.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerLimitTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private const string P1 = "p1", P2 = "p2";
    private static readonly string Server = "server:" + Guid.Parse("d0000000-0000-0000-0000-00000000000d").ToString("N");
    private static readonly StaffAction Owner = new("Owner", "test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TransferRequest Pay(Guid from, long amount, string? key = P1, Guid? to = null) =>
        new(Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(from), AccountRef.Resident(to ?? (from == B ? A : B)), amount, Region, KeyId: key);

    private static HoldRequest Buy(Guid buyer, long amount, string? key = P1) =>
        new(Guid.NewGuid(), "object", AccountRef.Resident(buyer), AccountRef.Resident(B), amount, Region, Guid.NewGuid(), "dress", key);

    private static ChargeRequest Fee(Guid payer, long amount, string? key = P1) =>
        new(Guid.NewGuid(), EntryType.UploadFee, AccountRef.Resident(payer), amount, null, key);

    private static PayoutRequest Payout(Guid owner, long amount, string? key = P1) =>
        new(Guid.NewGuid(), owner, AccountRef.Resident(B), amount, Region, Guid.NewGuid(), "slot", key);

    private static async Task Fund(MoneyLedger ledger, Guid who, long amount) =>
        Assert.Equal(MoneyOutcome.Ok, (await ledger.GiveAsync(AccountRef.Resident(who), amount, Owner, Ct)).Outcome);

    private static async Task<MoneyOutcome> Outcome(Task<MoneyResult> call) => (await call).Outcome;

    [Fact]
    public async Task Person_over_10000_in_24h_is_over_daily_limit()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);
        await Fund(ledger, A, 20_000);

        // Every kind of region spending counts: a payment, a hold that completes, a fee.
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 6_000), Ct)));
        var hold = await ledger.HoldAsync(Buy(A, 3_000), Ct);
        Assert.Equal(MoneyOutcome.Ok, hold.Outcome);
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.ChargeAsync(Fee(A, 1_000), Ct)));

        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.TransferAsync(Pay(A, 1), Ct)));
        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.HoldAsync(Buy(A, 1), Ct)));
        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.ChargeAsync(Fee(A, 1), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(B, 1), Ct))); // someone else's limit is their own
        Assert.Equal(new LimitView(10_000, 5_000, false, false, null, 10_000, 0), await ledger.PersonLimitsAsync(AccountRef.Resident(A), Ct));
    }

    [Fact]
    public async Task Returned_hold_does_not_count()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);
        await Fund(ledger, A, 20_000);
        var hold = await ledger.HoldAsync(Buy(A, 8_000), Ct);
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.ReturnHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)));

        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 10_000), Ct)));
        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.TransferAsync(Pay(A, 1), Ct)));
        Assert.Equal(10_000, (await ledger.PersonLimitsAsync(AccountRef.Resident(A), Ct))!.SpentToday);
        Assert.Equal(10_000, (await ledger.LimitsAsync(LimitTarget.ForKey(P1), Ct))!.SpentToday);
    }

    [Fact]
    public async Task Staff_gift_does_not_count()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);
        await Fund(ledger, A, 30_000);
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TakeAsync(AccountRef.Resident(A), 9_000, Owner, Ct)));

        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 10_000), Ct)));
        Assert.Equal(10_000, (await ledger.PersonLimitsAsync(AccountRef.Resident(A), Ct))!.SpentToday);
    }

    [Fact]
    public async Task Key_over_100000_is_over_key_limit()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, DailySpendLimit: 1_000_000));
        await Fund(ledger, A, 200_000);
        await Fund(ledger, B, 200_000);

        // All of one key's payers together.
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 60_000), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(B, 40_000), Ct)));

        Assert.Equal(MoneyOutcome.OverKeyLimit, await Outcome(ledger.TransferAsync(Pay(A, 1), Ct)));
        Assert.Equal(MoneyOutcome.OverKeyLimit, await Outcome(ledger.HoldAsync(Buy(A, 1), Ct)));
        Assert.Equal(MoneyOutcome.OverKeyLimit, await Outcome(ledger.ChargeAsync(Fee(A, 1), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 1, P2), Ct)));
        Assert.Equal(new LimitView(100_000, 20_000, false, false, null, 100_000, 0), await ledger.LimitsAsync(LimitTarget.ForKey(P1), Ct));
    }

    [Fact]
    public async Task Key_payouts_over_20000_is_over_key_limit()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, DailyPayoutLimit: 1_000_000));
        await Fund(ledger, A, 50_000);
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);

        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.PayoutAsync(Payout(A, 15_000), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.PayoutAsync(Payout(A, 5_000), Ct)));

        Assert.Equal(MoneyOutcome.OverKeyLimit, await Outcome(ledger.PayoutAsync(Payout(A, 1), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.PayoutAsync(Payout(A, 1, P2), Ct)));
        // Payouts are the key's payouts, not its spending.
        Assert.Equal(new LimitView(100_000, 20_000, false, false, null, 0, 20_000), await ledger.LimitsAsync(LimitTarget.ForKey(P1), Ct));
    }

    [Fact]
    public async Task Rolling_window_frees_the_limit_after_24h()
    {
        var (ledger, _, time) = await MoneyLedgerTransferTests.Setup(maria);
        await Fund(ledger, A, 30_000);
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 10_000), Ct)));

        time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.TransferAsync(Pay(A, 1), Ct)));

        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 10_000), Ct)));
        Assert.Equal(10_000, (await ledger.PersonLimitsAsync(AccountRef.Resident(A), Ct))!.SpentToday);
    }

    [Fact]
    public async Task Person_override_higher_allows_more_and_ends_on_until()
    {
        var (ledger, _, time) = await MoneyLedgerTransferTests.Setup(maria);
        await Fund(ledger, A, 50_000);
        var until = time.GetUtcNow().UtcDateTime.AddHours(2);

        Assert.Equal(MoneyOutcome.Ok, await ledger.SetPersonLimitsAsync(AccountRef.Resident(A), 20_000, null, until, Owner, Ct));
        Assert.Equal(new LimitView(20_000, 5_000, true, false, until, 0, 0), await ledger.PersonLimitsAsync(AccountRef.Resident(A), Ct));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 15_000), Ct)));
        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.TransferAsync(Pay(A, 5_001), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 5_000), Ct)));

        time.Advance(TimeSpan.FromHours(3));
        Assert.Equal(new LimitView(10_000, 5_000, false, false, null, 20_000, 0), await ledger.PersonLimitsAsync(AccountRef.Resident(A), Ct));
        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.TransferAsync(Pay(A, 1), Ct)));
    }

    [Fact]
    public async Task Person_override_lower_refuses_sooner()
    {
        var (ledger, _, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);

        Assert.Equal(MoneyOutcome.Ok, await ledger.SetPersonLimitsAsync(AccountRef.Resident(A), 50, 20, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 40), Ct)));
        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.TransferAsync(Pay(A, 20), Ct)));
        // The person's own script payout limit replaces the per-resident one.
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.PayoutAsync(Payout(A, 20), Ct)));
        Assert.Equal(MoneyOutcome.OverDailyLimit, await Outcome(ledger.PayoutAsync(Payout(A, 1), Ct)));

        // Back to the defaults.
        Assert.Equal(MoneyOutcome.Ok, await ledger.SetPersonLimitsAsync(AccountRef.Resident(A), null, null, null, Owner, Ct));
        Assert.Equal(new LimitView(10_000, 5_000, false, false, null, 40, 20), await ledger.PersonLimitsAsync(AccountRef.Resident(A), Ct));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 20), Ct)));
    }

    [Fact]
    public async Task Key_override_applies_to_that_key_only()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);

        Assert.Equal(MoneyOutcome.Ok, await ledger.SetLimitsAsync(LimitTarget.ForKey(Server), 50, 10, null, Owner, Ct));
        Assert.Equal(1, await db.Scalar($"SELECT COUNT(*) FROM `money`.`limit_overrides` WHERE kind = 'key' AND target = '{Server}' AND reason = 'test' AND staff = 'Owner'"));

        Assert.Equal(MoneyOutcome.OverKeyLimit, await Outcome(ledger.TransferAsync(Pay(A, 60, Server), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 60, P2), Ct)));
        Assert.Equal(MoneyOutcome.OverKeyLimit, await Outcome(ledger.PayoutAsync(Payout(A, 11, Server), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.PayoutAsync(Payout(A, 11, P2), Ct)));
        Assert.Equal(new LimitView(50, 10, true, true, null, 0, 0), await ledger.LimitsAsync(LimitTarget.ForKey(Server), Ct));
        Assert.Equal(new LimitView(100_000, 20_000, false, false, null, 60, 11), await ledger.LimitsAsync(LimitTarget.ForKey(P2), Ct));
    }

    [Fact]
    public async Task Two_racing_payments_by_one_key_cannot_pass_its_limit()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, DailyKeySpendLimit: 100));
        await Fund(ledger, A, 1_000);
        await Fund(ledger, B, 1_000);
        var results = new ConcurrentBag<MoneyOutcome>();

        // Two payers, so only the key's lock keeps them apart.
        await Parallel.ForEachAsync(Enumerable.Range(0, 10), new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = Ct },
            async (i, ct) => results.Add((await ledger.TransferAsync(Pay(i % 2 == 0 ? A : B, 30), ct)).Outcome));

        Assert.Equal(3, results.Count(o => o == MoneyOutcome.Ok));
        Assert.All(results, o => Assert.Contains(o, new[] { MoneyOutcome.Ok, MoneyOutcome.OverKeyLimit }));
        Assert.Equal(90, await db.Scalar($"SELECT SUM(amount) FROM `money`.`entries` WHERE key_id = '{P1}'"));
        Assert.True((await ledger.SelfCheckAsync(Ct)).Ok);
    }

    [Fact]
    public async Task Entries_record_the_key()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);

        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.TransferAsync(Pay(A, 5), Ct)));
        var hold = await ledger.HoldAsync(Buy(A, 5), Ct);
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.ChargeAsync(Fee(A, 5, Server), Ct)));
        Assert.Equal(MoneyOutcome.Ok, await Outcome(ledger.PayoutAsync(Payout(A, 5, P2), Ct)));
        await Fund(ledger, A, 5);

        Assert.Equal(P1, await db.Text("SELECT key_id FROM `money`.`entries` WHERE type = 'pay_avatar'"));
        Assert.Equal(P1, await db.Text("SELECT key_id FROM `money`.`entries` WHERE type = 'hold'"));
        Assert.Equal(P1, await db.Text($"SELECT key_id FROM `money`.`holds` WHERE id = {hold.HoldId}"));
        Assert.Equal(P1, await db.Text("SELECT key_id FROM `money`.`entries` WHERE type = 'hold_completed'"));
        Assert.Equal(Server, await db.Text("SELECT key_id FROM `money`.`entries` WHERE type = 'upload_fee'"));
        Assert.Equal(P2, await db.Text("SELECT key_id FROM `money`.`entries` WHERE type = 'script_payout'"));
        // What a key didn't start has no key: starting balances, staff gifts.
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type IN ('starting_balance', 'staff_gift') AND key_id IS NOT NULL"));
        Assert.True((await ledger.SelfCheckAsync(Ct)).Ok);
    }

    [Fact]
    public async Task Owner_limits_must_be_0_to_100_million_with_a_reason()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        var key = LimitTarget.ForKey(P1);
        var a = AccountRef.Resident(A);

        Assert.Equal(MoneyOutcome.BadAmount, await ledger.SetLimitsAsync(key, -1, null, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.BadAmount, await ledger.SetLimitsAsync(key, null, -1, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.BadAmount, await ledger.SetLimitsAsync(key, MoneyLedger.MaxDailyLimit + 1, null, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.BadAmount, await ledger.SetLimitsAsync(key, null, long.MaxValue, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.BadAmount, await ledger.SetPersonLimitsAsync(a, -1, null, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.BadAmount, await ledger.SetPersonLimitsAsync(a, null, MoneyLedger.MaxDailyLimit + 1, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.BadAmount, await ledger.SetPersonLimitsAsync(a, 5, null, null, new StaffAction("Owner", " "), Ct));
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`limit_overrides`"));

        Assert.Equal(MoneyOutcome.Ok, await ledger.SetLimitsAsync(key, MoneyLedger.MaxDailyLimit, 0, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.Ok, await ledger.SetPersonLimitsAsync(a, 0, MoneyLedger.MaxDailyLimit, null, Owner, Ct));
        Assert.Equal(MoneyOutcome.NoWallet, await ledger.SetPersonLimitsAsync(AccountRef.Resident(Guid.NewGuid()), 5, null, null, Owner, Ct));
        Assert.Null(await ledger.PersonLimitsAsync(AccountRef.Resident(Guid.NewGuid()), Ct));
    }
}
