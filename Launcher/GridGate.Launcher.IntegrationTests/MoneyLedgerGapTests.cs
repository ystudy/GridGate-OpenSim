using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Money;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// Ledger tests money design §12 asks for that were partial or missing (money review 2026-11-15, L-8): an unfinished hold
/// returning on its own while payments are frozen, many kinds of debit racing on one payer, and two copies of one
/// transaction id racing.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerGapTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("d0000000-0000-0000-0000-00000000000d");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HoldRequest Buy(long amount, Guid? seller = null, Guid? txn = null) =>
        new(txn ?? Guid.NewGuid(), "object", AccountRef.Resident(A), AccountRef.Resident(seller ?? B), amount, Region, Guid.NewGuid(), "dress");

    private static TransferRequest Pay(Guid to, long amount, Guid? txn = null) =>
        new(txn ?? Guid.NewGuid(), EntryType.PayAvatar, AccountRef.Resident(A), AccountRef.Resident(to), amount, Region);

    private static Task<long> Of(MoneyTestDatabase db, Guid id) => db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{id}'");

    private static async Task AssertSound(MoneyLedger ledger, MoneyTestDatabase db)
    {
        var check = await ledger.SelfCheckAsync(Ct);
        Assert.True(check.Ok, string.Join(" ", check.Problems));
        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE id <> {MoneySchema.BankId} AND balance < 0"));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task An_unfinished_hold_returns_on_its_own_while_payments_are_frozen(bool grid, bool buyer, bool seller)
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(40), Ct);
        if (grid) await ledger.SetFrozenAsync(true, "looking into something", Ct);
        if (buyer) await ledger.SetAccountFrozenAsync(AccountRef.Resident(A), true, Ct);
        if (seller) await ledger.SetAccountFrozenAsync(AccountRef.Resident(B), true, Ct);

        time.Advance(MoneyLedger.HoldLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(1, await ledger.ReturnExpiredHoldsAsync(Ct));
        Assert.Equal("returned", await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}"));
        Assert.Equal(100, await Of(db, A));
        Assert.Equal(0, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE id = {MoneySchema.HoldingId}"));
        await AssertSound(ledger, db);
    }

    [Fact]
    public async Task The_sweep_returns_an_unfinished_hold_while_payments_are_frozen()
    {
        var (ledger, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        var hold = await ledger.HoldAsync(Buy(40), Ct);
        await ledger.SetFrozenAsync(true, "looking into something", Ct);
        using var maintenance = new MoneyMaintenance(ledger, () => MoneyMode.Play, time, NullLogger<MoneyMaintenance>.Instance);
        await maintenance.StartAsync(Ct);

        time.Advance(MoneyLedger.HoldLifetime + TimeSpan.FromMinutes(1));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await db.Text($"SELECT state FROM `money`.`holds` WHERE id = {hold.HoldId}") != "returned")
        {
            Assert.True(DateTime.UtcNow < deadline, "The sweep didn't return the hold.");
            time.Advance(MoneyMaintenance.SweepEvery);
            await Task.Delay(100, Ct);
        }
        await maintenance.StopAsync(Ct);
        Assert.Equal(100, await Of(db, A));
        Assert.True((await ledger.FrozenAsync(Ct)).Frozen);
    }

    [Fact]
    public async Task Every_kind_of_debit_racing_on_one_payer_never_overdraws_it()
    {
        // The §12 race covered one payee and transfers only, and never ran the self-check.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria,
            new MoneySettings(Mode: MoneyMode.Play, ScriptPayouts: true, DailyPayoutLimit: 1_000, UploadFee: 1));
        await db.AddUser(C);
        await ledger.SetPayoutsAllowedAsync(A, true, Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);
        await ledger.BalanceAsync(AccountRef.Resident(C), Ct);

        var work = Enumerable.Range(0, 80).Select(i => Task.Run<(string Kind, MoneyResult Result)>(async () =>
        {
            var to = i % 2 == 0 ? B : C;
            switch (i % 4)
            {
                case 0: return ("transfer", await ledger.TransferAsync(Pay(to, 3), Ct));
                case 1: return ("payout", await ledger.PayoutAsync(new PayoutRequest(Guid.NewGuid(), A, AccountRef.Resident(to), 3, Region, Guid.NewGuid(), "slot"), Ct));
                case 2: return ("charge", await ledger.ChargeAsync(new ChargeRequest(Guid.NewGuid(), EntryType.UploadFee, AccountRef.Resident(A), 3, null), Ct));
                default:
                    var hold = await ledger.HoldAsync(Buy(3, to), Ct);
                    if (hold.Outcome != MoneyOutcome.Ok) return ("hold", hold);
                    // Half the purchases are delivered, half fail and come back.
                    var settled = i % 8 == 3
                        ? await ledger.CompleteHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct)
                        : await ledger.ReturnHoldAsync(Guid.NewGuid(), hold.HoldId!.Value, Ct);
                    Assert.Equal(MoneyOutcome.Ok, settled.Outcome);
                    return (i % 8 == 3 ? "sale" : "returned", hold);
            }
        })).ToList();
        var results = await Task.WhenAll(work);

        Assert.All(results, r => Assert.Contains(r.Result.Outcome, new[] { MoneyOutcome.Ok, MoneyOutcome.NotEnough }));
        // Every debit that stuck took 3; a returned purchase gave its 3 back.
        var stuck = results.Count(r => r.Result.Outcome == MoneyOutcome.Ok && r.Kind != "returned");
        Assert.Equal(100 - 3 * stuck, await Of(db, A));
        // Not "0 to 2": a returned purchase gives its coins back after other debits may have been refused for want of them.
        Assert.True(await Of(db, A) >= 0);
        Assert.Equal(0, await db.Scalar($"SELECT COUNT(*) FROM `money`.`holds` WHERE state = 'open'"));
        await AssertSound(ledger, db);
    }

    [Fact]
    public async Task Two_copies_of_one_transfer_racing_pay_once()
    {
        // The duplicate-key path in RunAsync: copies that all start before the first one commits.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);
        var request = Pay(B, 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ledger.TransferAsync(request, Ct))));

        Assert.All(results, r => Assert.Equal(MoneyOutcome.Ok, r.Outcome));
        Assert.Single(results.Select(r => r.EntryId).Distinct());
        Assert.Equal(90, await Of(db, A));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'pay_avatar'"));
        await AssertSound(ledger, db);
    }

    [Fact]
    public async Task Two_copies_of_one_hold_racing_hold_once()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);
        var request = Buy(10);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => ledger.HoldAsync(request, Ct))));

        Assert.All(results, r => Assert.Equal(MoneyOutcome.Ok, r.Outcome));
        Assert.Single(results.Select(r => r.HoldId).Distinct());
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`holds`"));
        Assert.Equal(90, await Of(db, A));
        await AssertSound(ledger, db);
    }

    [Fact]
    public async Task One_transaction_id_reused_for_another_amount_while_racing_pays_at_most_once()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        await ledger.BalanceAsync(AccountRef.Resident(B), Ct);
        var txn = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => ledger.TransferAsync(Pay(B, 1 + i % 2, txn), Ct))));

        Assert.All(results, r => Assert.Contains(r.Outcome, new[] { MoneyOutcome.Ok, MoneyOutcome.DuplicateTxn }));
        Assert.Equal(1, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'pay_avatar'"));
        await AssertSound(ledger, db);
    }

    /// <summary>A real database exception (MySqlException can't be built directly), from a statement that fails.</summary>
    private static async Task<Exception> DatabaseError(MoneyTestDatabase db)
    {
        await using var conn = await db.OpenMoney(Ct);
        await using var cmd = new MySqlConnector.MySqlCommand("SELECT * FROM no_such_table", conn);
        return await Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => cmd.ExecuteNonQueryAsync(Ct));
    }

    /// <summary>A ledger whose commit fails after <paramref name="landed"/> says whether it went through; once it has failed
    /// the database can be made unreachable too.</summary>
    private static async Task<(MoneyLedger Ledger, MoneyTestDatabase Db)> CommitFailing(MoneyMariaDb maria, bool landed, bool thenUnreachable)
    {
        var (_, db, time) = await MoneyLedgerTransferTests.Setup(maria);
        var down = false;
        var failure = await DatabaseError(db);
        var ledger = new MoneyLedger(
            async ct => down ? throw failure : await db.OpenMoney(ct),
            () => new MoneySettings(Mode: MoneyMode.Play, Visitors: VisitorMoney.All), () => MoneyMode.Play,
            MoneyLedgerTransferTests.LocalUsers(db), MoneyLedgerTransferTests.KnownVisitors(db), time);
        ledger.CommitForTests = async (tx, ct) =>
        {
            if (landed) await tx.CommitAsync(ct);
            else await tx.RollbackAsync(ct);
            down = thenUnreachable;
            throw failure;
        };
        return (ledger, db);
    }

    [Fact]
    public async Task A_commit_that_fails_but_landed_answers_what_was_stored()
    {
        var (ledger, db) = await CommitFailing(maria, landed: true, thenUnreachable: false);

        var r = await ledger.TransferAsync(Pay(B, 30), Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        Assert.Equal(70, r.FromBalance);
        Assert.Equal(30, await Of(db, B) - 100);
    }

    [Fact]
    public async Task A_commit_that_fails_and_did_not_land_answers_unavailable()
    {
        var (ledger, db) = await CommitFailing(maria, landed: false, thenUnreachable: false);

        var r = await ledger.TransferAsync(Pay(B, 30), Ct);

        Assert.Equal(MoneyOutcome.Unavailable, r.Outcome);
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries` WHERE type = 'pay_avatar'"));
    }

    [Fact]
    public async Task A_commit_that_fails_when_the_database_is_then_unreachable_answers_outcome_unknown()
    {
        var (ledger, _) = await CommitFailing(maria, landed: true, thenUnreachable: true);

        var r = await ledger.TransferAsync(Pay(B, 30), Ct);

        Assert.Equal(MoneyOutcome.OutcomeUnknown, r.Outcome);
    }
}
