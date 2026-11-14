using System;
using System.Threading;
using System.Threading.Tasks;

namespace GridGate.Region.Money;

/// <summary>
/// The money service for a Region Server that isn't paired with its grid's money yet (money design §7.7): every call is
/// refused with <c>not_paired</c>, so the region takes no payment and says why. Nothing leaves the region.
/// </summary>
public sealed class UnpairedMoneyService : IMoneyService
{
    public const string Code = "not_paired";

    private static readonly Task<MoneyAnswer> Refused = Task.FromResult(new MoneyAnswer(Code, null, null, null, null));

    public Task<MoneyAnswer> BalanceAsync(MoneyWho who, CancellationToken ct) => Refused;

    public Task<MoneyAnswer> TransferAsync(Guid txn, string type, MoneyWho from, MoneyWho to, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct) => Refused;

    public Task<MoneyAnswer> PayoutAsync(Guid txn, Guid ownerId, MoneyWho to, long amount, Guid regionId, Guid objectId, string objectName, CancellationToken ct) =>
        Refused;

    public Task<MoneyAnswer> ChargeAsync(Guid txn, string type, MoneyWho payer, long amount, Guid? payTo, CancellationToken ct) => Refused;

    public Task<MoneyAnswer> HoldAsync(Guid txn, string kind, MoneyWho buyer, MoneyWho seller, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct) => Refused;

    public Task<MoneyAnswer> CompleteAsync(Guid txn, long holdId, CancellationToken ct) => Refused;

    public Task<MoneyAnswer> ReturnAsync(Guid txn, long holdId, CancellationToken ct) => Refused;

    public Task<EconomyInfo?> EconomyAsync(CancellationToken ct) => Task.FromResult<EconomyInfo?>(null);
}
