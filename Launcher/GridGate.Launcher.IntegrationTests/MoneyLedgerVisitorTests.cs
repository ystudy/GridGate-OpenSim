using GridGate.Launcher.Core.Money;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// Hypergrid visitors' wallets (money plan 3, task 1): only for a home the Gatekeeper recorded, and closed while visitor
/// wallets are off.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyLedgerVisitorTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>
{
    private static readonly Guid A = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid V = MoneyLedgerTransferTests.V;
    private static readonly StaffAction Staff = new("Test Owner", "testing");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TransferRequest Pay(AccountRef from, AccountRef to, long amount) =>
        new(Guid.NewGuid(), EntryType.PayAvatar, from, to, amount);

    private static Task<long> Accounts(MoneyTestDatabase db, Guid id) =>
        db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE agent_id = '{id}'");

    [Fact]
    public async Task Unknown_visitor_home_gets_no_wallet()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.BalanceAsync(new AccountRef(V, "http://made.up/"), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.TransferAsync(Pay(AccountRef.Resident(A), new AccountRef(V, "http://made.up/"), 5), Ct)).Outcome);
        var stranger = Guid.NewGuid();
        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.BalanceAsync(new AccountRef(stranger, "http://other.example:8002"), Ct)).Outcome);

        Assert.Equal(0, await Accounts(db, V));
        Assert.Equal(0, await Accounts(db, stranger));
    }

    [Fact]
    public async Task Known_visitor_home_matches_without_case_or_slash()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var w = Guid.NewGuid();
        await db.AddVisitor(w, "http://Grid.Example:8002/");

        var r = await ledger.BalanceAsync(new AccountRef(w, "http://grid.example:8002"), Ct);

        Assert.Equal(MoneyOutcome.Ok, r.Outcome);
        Assert.Equal(0, r.FromBalance);
        Assert.Equal(1, await Accounts(db, w));
    }

    [Fact]
    public async Task Visitor_wallets_off_refuses_both_sides()
    {
        var settings = new MoneySettings(Mode: MoneyMode.Play);
        var current = settings;
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await db.AddUser(A);
        await db.AddVisitor(V, "http://other.example:8002/");
        var ledger = new MoneyLedger(db.OpenMoney, () => current, () => MoneyMode.Play, MoneyLedgerTransferTests.LocalUsers(db),
            MoneyLedgerTransferTests.KnownVisitors(db), new FakeTimeProvider(DateTimeOffset.UtcNow));
        var visitor = new AccountRef(V, "http://other.example:8002");
        Assert.Equal(MoneyOutcome.Ok, (await ledger.GiveAsync(visitor, 10, Staff, Ct)).Outcome);
        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);

        current = settings with { VisitorWallets = false };

        Assert.Equal(MoneyOutcome.VisitorsOff, (await ledger.TransferAsync(Pay(visitor, AccountRef.Resident(A), 5), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.VisitorsOff, (await ledger.TransferAsync(Pay(AccountRef.Resident(A), visitor, 5), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.VisitorsOff, (await ledger.BalanceAsync(visitor, Ct)).Outcome);
        Assert.Equal(10, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{V}'"));
        Assert.Equal(100, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{A}'"));
    }

    [Fact]
    public async Task Visitor_wallets_off_still_lets_staff_take_back()
    {
        var current = new MoneySettings(Mode: MoneyMode.Play);
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        await db.AddVisitor(V, "http://other.example:8002/");
        var ledger = new MoneyLedger(db.OpenMoney, () => current, () => MoneyMode.Play, MoneyLedgerTransferTests.LocalUsers(db),
            MoneyLedgerTransferTests.KnownVisitors(db), new FakeTimeProvider(DateTimeOffset.UtcNow));
        var visitor = new AccountRef(V, "http://other.example:8002");
        await ledger.GiveAsync(visitor, 10, Staff, Ct);
        current = current with { VisitorWallets = false };

        Assert.Equal(MoneyOutcome.Ok, (await ledger.TakeAsync(visitor, 10, Staff, Ct)).Outcome);
        Assert.Equal(0, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{V}'"));
    }

    [Fact]
    public async Task Visitor_wallets_off_makes_no_new_wallet_even_for_staff()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria, new MoneySettings(Mode: MoneyMode.Play, VisitorWallets: false));

        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.GiveAsync(new AccountRef(V, "http://other.example:8002"), 5, Staff, Ct)).Outcome);
        Assert.Equal(0, await Accounts(db, V));
    }

    [Fact]
    public async Task Staff_may_open_a_wallet_robust_has_no_record_of()
    {
        // OpenSim keeps one GridUser row per UUID, so a visitor's new home is never recorded; staff (admin-only, a reason,
        // audited) are the way to a wallet there.
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);

        Assert.Equal(MoneyOutcome.Ok, (await ledger.GiveAsync(new AccountRef(V, "http://new.example:8002/"), 5, Staff, Ct)).Outcome);
        Assert.Equal(1, await Accounts(db, V));
    }

    [Fact]
    public async Task A_returning_visitors_new_home_is_not_known()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        await db.AddVisitor(V, "http://new.example:8002/"); // ignored: the first home stays

        Assert.Equal(MoneyOutcome.NoWallet, (await ledger.TransferAsync(Pay(AccountRef.Resident(A), new AccountRef(V, "http://new.example:8002"), 5), Ct)).Outcome);
        Assert.Equal(0, await Accounts(db, V));
    }

    [Fact]
    public async Task Merge_to_a_new_home_opens_it()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        var oldHome = new AccountRef(V, "http://other.example:8002");
        await ledger.GiveAsync(oldHome, 12, Staff, Ct);

        Assert.Equal(MoneyOutcome.Ok, (await ledger.MergeWalletsAsync(oldHome, new AccountRef(V, "http://moved.example:8002/"), Staff, Ct)).Outcome);

        Assert.Equal(12, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{V}' AND home = 'http://moved.example:8002'"));
        // Payments now find the new wallet without any GridUser row for it.
        Assert.Equal(MoneyOutcome.Ok, (await ledger.TransferAsync(Pay(AccountRef.Resident(A), new AccountRef(V, "http://moved.example:8002"), 5), Ct)).Outcome);
    }

    [Fact]
    public async Task Find_balance_never_creates()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        IMoneyBooks books = ledger;

        Assert.Equal(MoneyOutcome.NoWallet, (await books.FindBalanceAsync(AccountRef.Resident(A), Ct)).Outcome);
        Assert.Equal(MoneyOutcome.NoWallet, (await books.FindBalanceAsync(new AccountRef(V, "http://other.example:8002"), Ct)).Outcome);
        Assert.Equal(0, await Accounts(db, A));
        Assert.Equal(0, await db.Scalar("SELECT COUNT(*) FROM `money`.`entries`"));

        await ledger.BalanceAsync(AccountRef.Resident(A), Ct);
        var found = await books.FindBalanceAsync(AccountRef.Resident(A), Ct);
        Assert.Equal(MoneyOutcome.Ok, found.Outcome);
        Assert.Equal(100, found.FromBalance);
    }

    [Fact]
    public async Task Merge_goes_through_the_books()
    {
        var (ledger, db, _) = await MoneyLedgerTransferTests.Setup(maria);
        IMoneyBooks books = ledger;
        var oldHome = new AccountRef(V, "http://old.example:8002");
        var newHome = new AccountRef(V, "http://new.example:8002");
        await ledger.GiveAsync(oldHome, 25, Staff, Ct);

        Assert.Equal(MoneyOutcome.Ok, (await books.MergeWalletsAsync(oldHome, newHome, Staff, Ct)).Outcome);
        Assert.Equal(25, await db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{V}' AND home = 'http://new.example:8002'"));
        Assert.Equal(MoneyOutcome.NoWallet, (await books.MergeWalletsAsync(new AccountRef(V, "http://never.example"), newHome, Staff, Ct)).Outcome);
    }

    [Fact]
    public async Task Known_visitor_check_failing_is_unavailable()
    {
        var db = await maria.FreshAsync();
        await db.EnsureSchemaAsync();
        var s = new MoneySettings(Mode: MoneyMode.Play);
        var closed = db.Settings with { MariaDbPort = MoneyMariaDb.FreePort() };
        var robustDown = new PresenceCheck(ct => Core.Database.GridDb.OpenRobustAsync(closed, 2, ct));
        var ledger = new MoneyLedger(db.OpenMoney, () => s, () => MoneyMode.Play, MoneyLedgerTransferTests.LocalUsers(db),
            robustDown.IsKnownVisitorAsync, new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(MoneyOutcome.Unavailable, (await ledger.BalanceAsync(new AccountRef(V, "http://other.example:8002"), Ct)).Outcome);
        Assert.Equal(0, await Accounts(db, V));
    }
}
