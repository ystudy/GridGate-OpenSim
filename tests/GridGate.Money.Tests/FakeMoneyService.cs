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

    /// <summary>Awaited before a method answers (after the call is recorded): lets a test hold a call open.</summary>
    public Func<string, Task>? Gate { get; set; }

    private async Task<MoneyAnswer> Record(string method, Guid txn, CancellationToken ct, params object?[] args)
    {
        Calls.Enqueue(new Call(method, txn, args));
        lock (Order) Order.Add(method);
        if (Gate is { } gate) await gate(method).WaitAsync(ct);
        lock (_queued)
            return _queued.TryGetValue(method, out var q) && q.Count > 0 ? q.Dequeue() : Answer(method);
    }

    public Task<MoneyAnswer> BalanceAsync(MoneyWho who, CancellationToken ct) => Record("balance", Guid.Empty, ct, who);

    public Task<MoneyAnswer> TransferAsync(Guid txn, string type, MoneyWho from, MoneyWho to, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct) => Record("transfer", txn, ct, type, from, to, amount, regionId, objectId, objectName);

    public Task<MoneyAnswer> PayoutAsync(Guid txn, Guid ownerId, MoneyWho to, long amount, Guid regionId, Guid objectId, string objectName, CancellationToken ct) =>
        Record("payout", txn, ct, ownerId, to, amount, regionId, objectId, objectName);

    public Task<MoneyAnswer> ChargeAsync(Guid txn, string type, MoneyWho payer, long amount, Guid? payTo, Guid regionId, CancellationToken ct) =>
        Record("charge", txn, ct, type, payer, amount, payTo, regionId);

    public Task<MoneyAnswer> HoldAsync(Guid txn, string kind, MoneyWho buyer, MoneyWho? seller, long amount, Guid regionId, Guid? objectId, string? objectName,
        CancellationToken ct) => Record("hold", txn, ct, kind, buyer, seller, amount, regionId, objectId, objectName);

    public Task<MoneyAnswer> CompleteAsync(Guid txn, long holdId, CancellationToken ct) => Record("complete", txn, ct, holdId);

    public Task<MoneyAnswer> ReturnAsync(Guid txn, long holdId, CancellationToken ct) => Record("return", txn, ct, holdId);

    public Task<EconomyInfo?> EconomyAsync(CancellationToken ct) => Task.FromResult(Economy);

    /// <summary>
    /// The place reports (health, handoffs, cancels and arrivals), kept apart from <see cref="Calls"/> and <see cref="Order"/>:
    /// a region reports them on its own whenever someone arrives, whatever a test is paying for.
    /// </summary>
    public ConcurrentQueue<Call> Places { get; } = new();

    public IEnumerable<Call> PlacesOf(string method) => Places.Where(c => c.Method == method);

    private async Task<MoneyAnswer> Place(string method, Guid id, CancellationToken ct, params object?[] args)
    {
        Places.Enqueue(new Call(method, id, args));
        if (Gate is { } gate) await gate(method).WaitAsync(ct);
        lock (_queued)
            return _queued.TryGetValue(method, out var q) && q.Count > 0 ? q.Dequeue() : Answer(method);
    }

    public Task<MoneyAnswer> HealthAsync(int protocol, IReadOnlyList<Guid> regions, CancellationToken ct) =>
        Place("health", Guid.Empty, ct, protocol, regions.ToArray());

    public Task<MoneyAnswer> HandoffAsync(Guid id, Guid sessionId, Guid fromRegion, Guid toRegion, string kind, CancellationToken ct) =>
        Place("handoff", id, ct, sessionId, fromRegion, toRegion, kind);

    public Task<MoneyAnswer> CancelHandoffAsync(Guid sessionId, Guid fromRegion, CancellationToken ct) => Place("cancel", Guid.Empty, ct, sessionId, fromRegion);

    public Task<MoneyAnswer> ArrivedAsync(Guid id, Guid sessionId, Guid agentId, Guid regionId, CancellationToken ct) =>
        Place("arrived", id, ct, sessionId, agentId, regionId);
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

    /// <summary>When set, <see cref="Delay"/> really waits (and can be cancelled) instead of returning at once.</summary>
    public bool Waits { get; set; }

    public Task Delay(TimeSpan t, CancellationToken ct)
    {
        lock (this) Delays.Add(t);
        return Waits ? Task.Delay(t, ct) : Task.CompletedTask;
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
