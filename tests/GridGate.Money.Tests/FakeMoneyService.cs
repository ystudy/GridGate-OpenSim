using System.Collections.Concurrent;
using GridGate.Region.Money;

namespace GridGate.Money.Tests;

/// <summary>One call the module made: its method, transaction id and arguments.</summary>
internal sealed record Call(string Method, Guid Txn, object?[] Args);

/// <summary>
/// The money service, faked: records every call and answers from <see cref="Answer"/> (by method), or from a queue of
/// answers per method when one is set.
/// </summary>
internal sealed class FakeMoneyService : IMoneyService
{
    public ConcurrentQueue<Call> Calls { get; } = new();
    public List<string> Order { get; } = [];
    private readonly Dictionary<string, Queue<MoneyAnswer>> _queued = new();

    /// <summary>The answer for a method when nothing is queued for it.</summary>
    public Func<string, MoneyAnswer> Answer { get; set; } = _ => new MoneyAnswer("ok", 0, 0, 1, 7);

    public EconomyInfo? Economy { get; set; } = new("C$", 0, 0, true, true, 1_000_000);

    public void Queue(string method, params MoneyAnswer[] answers)
    {
        if (!_queued.TryGetValue(method, out var q)) _queued[method] = q = new Queue<MoneyAnswer>();
        foreach (var a in answers) q.Enqueue(a);
    }

    public IEnumerable<Call> Of(string method) => Calls.Where(c => c.Method == method);

    private Task<MoneyAnswer> Record(string method, Guid txn, params object?[] args)
    {
        Calls.Enqueue(new Call(method, txn, args));
        lock (Order) Order.Add(method);
        lock (_queued)
            return Task.FromResult(_queued.TryGetValue(method, out var q) && q.Count > 0 ? q.Dequeue() : Answer(method));
    }

    public Task<MoneyAnswer> BalanceAsync(MoneyWho who, CancellationToken ct) => Record("balance", Guid.Empty, who);

    public Task<MoneyAnswer> TransferAsync(Guid txn, string type, MoneyWho from, MoneyWho to, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct) => Record("transfer", txn, type, from, to, amount, regionId, objectId, objectName);

    public Task<MoneyAnswer> PayoutAsync(Guid txn, Guid ownerId, MoneyWho to, long amount, Guid regionId, Guid objectId, string objectName, CancellationToken ct) =>
        Record("payout", txn, ownerId, to, amount, regionId, objectId, objectName);

    public Task<MoneyAnswer> ChargeAsync(Guid txn, string type, MoneyWho payer, long amount, Guid? payTo, CancellationToken ct) =>
        Record("charge", txn, type, payer, amount, payTo);

    public Task<MoneyAnswer> HoldAsync(Guid txn, string kind, MoneyWho buyer, MoneyWho seller, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct) => Record("hold", txn, kind, buyer, seller, amount, regionId, objectId, objectName);

    public Task<MoneyAnswer> CompleteAsync(Guid txn, long holdId, CancellationToken ct) => Record("complete", txn, holdId);

    public Task<MoneyAnswer> ReturnAsync(Guid txn, long holdId, CancellationToken ct) => Record("return", txn, holdId);

    public Task<EconomyInfo?> EconomyAsync(CancellationToken ct) => Task.FromResult(Economy);
}

/// <summary>The viewer and scene, faked: records what the actions told, pushed and raised.</summary>
internal sealed class FakeView : IMoneyView
{
    public List<(Guid Agent, string Text)> Told { get; } = [];
    public List<(Guid Agent, long Balance, BalanceNote? Note)> Pushed { get; } = [];
    public List<(Guid Part, Guid Payer, int Amount)> Paid { get; } = [];
    public List<TimeSpan> Delays { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Order { get; } = [];

    public void Tell(Guid agent, string text)
    {
        lock (this) { Told.Add((agent, text)); Order.Add("tell"); }
    }

    public void PushBalance(Guid agent, long balance, BalanceNote? note)
    {
        lock (this) { Pushed.Add((agent, balance, note)); Order.Add("push"); }
    }

    public void ObjectPaid(Guid partId, Guid payer, int amount)
    {
        lock (this) { Paid.Add((partId, payer, amount)); Order.Add("paid"); }
    }

    public Task Delay(TimeSpan t)
    {
        lock (this) Delays.Add(t);
        return Task.CompletedTask;
    }

    public void Warn(string text)
    {
        lock (this) Warnings.Add(text);
    }

    public void Error(string text)
    {
        lock (this) Errors.Add(text);
    }
}
