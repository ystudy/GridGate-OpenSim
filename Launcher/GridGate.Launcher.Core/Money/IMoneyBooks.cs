namespace GridGate.Launcher.Core.Money;

/// <summary>
/// What the website's money pages ask of the ledger (money design §9.1, §9.5). <see cref="MoneyLedger"/> is the one
/// implementation; the interface lets the pages be tested without a database.
/// </summary>
public interface IMoneyBooks
{
    Task<MoneyResult> BalanceAsync(AccountRef who, CancellationToken ct);
    Task<MoneyResult> FindBalanceAsync(AccountRef who, CancellationToken ct);
    Task<MoneyResult> MergeWalletsAsync(AccountRef from, AccountRef to, StaffAction who, CancellationToken ct);
    Task<IReadOnlyList<HistoryLine>> HistoryAsync(AccountRef who, int max, CancellationToken ct);
    Task<AccountFlags?> AccountFlagsAsync(AccountRef who, CancellationToken ct);
    Task<MoneyTotals> TotalsAsync(CancellationToken ct);
    Task<(bool Frozen, string Reason)> FrozenAsync(CancellationToken ct);
    Task SetFrozenAsync(bool frozen, string reason, CancellationToken ct);
    Task SetAccountFrozenAsync(AccountRef who, bool frozen, CancellationToken ct);
    Task<MoneyResult> GiveAsync(AccountRef to, long amount, StaffAction who, CancellationToken ct);
    Task<MoneyResult> TakeAsync(AccountRef from, long amount, StaffAction who, CancellationToken ct);
    Task<MoneyResult> MoveFeesAsync(AccountRef to, long amount, StaffAction who, CancellationToken ct);
    Task<MoneyResult> RefundAsync(long entryId, StaffAction who, CancellationToken ct);
    Task<MoneyResult> SetPayoutsAllowedAsync(Guid residentId, bool allowed, CancellationToken ct);
    Task<SelfCheckResult> SelfCheckAsync(CancellationToken ct);

    /// <summary>A person's or key's daily limits now; null for a person without a wallet (verified-regions design §8.7).</summary>
    Task<LimitView?> LimitsAsync(LimitTarget target, CancellationToken ct);
    Task<LimitView?> PersonLimitsAsync(AccountRef person, CancellationToken ct);

    /// <summary>The owner's own limits for one person or key; a null limit uses the grid default, and both null removes them.</summary>
    Task<MoneyOutcome> SetLimitsAsync(LimitTarget target, long? dailySpend, long? dailyPayout, DateTime? until, StaffAction staff, CancellationToken ct);
    Task<MoneyOutcome> SetPersonLimitsAsync(AccountRef person, long? dailySpend, long? dailyPayout, DateTime? until, StaffAction staff, CancellationToken ct);
}
