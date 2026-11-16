using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace GridGate.Region.Money;

/// <summary>
/// Tells the money service where avatars are: arrivals, handoffs (teleports, crossings, leaving the grid), failed
/// teleports and this program's health. Each event only queues its report and returns, so a teleport or crossing never
/// waits for the service. One loop sends them one at a time: the program never has more than one call open.
/// </summary>
/// <remarks>
/// <para>A teleport or crossing within the grid is handed off only once it has completed: when the region left lets the
/// avatar go, or, when the destination is one of this program's regions, when the avatar arrives there, ahead of that
/// arrival. A teleport or crossing that fails hands nobody over. A teleport to another grid is handed off when it starts.</para>
/// <para>Reports go oldest first, except that a waiting handoff goes ahead of other people's waiting arrivals. A send
/// answered "unavailable" is tried again after 1, 3, 9 and 17 seconds with the same id, then dropped; any other answer is
/// final. NPCs are never reported.</para>
/// <para>While the service can't be reached the queue is kept short. A report other than health that is older than
/// <see cref="MaxAge"/> is dropped unsent: its handoff has expired at the service, and a late arrival could only be
/// refused or pause someone's money. At most one health report waits; a newer one takes its place. Past
/// <see cref="MaxWaiting"/> waiting reports the oldest is dropped.</para>
/// </remarks>
internal sealed class PlaceReporter : IDisposable
{
    /// <summary>The money handoff protocol this module speaks: 2 is the first with handoffs.</summary>
    public const int Protocol = 2;

    /// <summary>The most reports that wait to be sent.</summary>
    public const int MaxWaiting = 1000;

    /// <summary>Past this many avatars in the middle of a teleport or crossing, the stale ones are forgotten.</summary>
    private const int MaxRemembered = 256;

    /// <summary>How long a report other than health stays worth sending: the service's handoff life.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    // Real time, not the injected clock: the clock itself may be what failed.
    private static readonly TimeSpan FailurePause = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(17)];

    private readonly IMoneyService _service;
    private readonly TimeProvider _time;
    private readonly Action<string> _warn;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    // Guarded by _gate: the reports waiting, oldest first; how many the cap dropped since the loop last said so.
    private readonly LinkedList<Report> _waiting = new();
    private readonly SemaphoreSlim _queued = new(0);
    // Guarded by _gate: by avatar, the teleport or crossing each is in the middle of, until it is handed off or fails.
    private readonly Dictionary<Guid, Leaving> _leaving = new();
    private int _overflowed;
    private bool _disposed;
    private TaskCompletionSource _idle = Idle();

    private enum Kind { Health, Handoff, Arrival, Other }

    /// <summary>
    /// One report: what it is, for the log (never who it is about), its kind and session, when it was queued, and how to send
    /// it, with its id fixed.
    /// </summary>
    private sealed record Report(string What, Kind Kind, Guid Session, DateTimeOffset Queued, Func<CancellationToken, Task<MoneyAnswer>> Send);

    /// <summary>A teleport or crossing (<paramref name="Kind"/>) of <paramref name="Session"/> from one region to another, and when it began.</summary>
    private sealed record Leaving(Guid Session, Guid From, Guid To, string Kind)
    {
        public DateTimeOffset At { get; init; }
    }

    /// <param name="warn">Told when reports are dropped; never with a session, an avatar or an id.</param>
    public PlaceReporter(IMoneyService service, TimeProvider time, Action<string> warn)
    {
        (_service, _time, _warn) = (service, time, warn);
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>
    /// An avatar became a root agent in its region (<c>OnMakeRootAgent</c>). When it came by a teleport or crossing from
    /// another of this program's regions, the arrival proves the move completed in a region this program runs: that move is
    /// handed off now, ahead of the arrival, rather than when the region left lets the avatar go. In a teleport the
    /// destination makes the avatar root first, and this program's one queue, sending one report at a time, would otherwise
    /// send the arrival, held at the service for its handoff, ahead of the handoff it waits for.
    /// </summary>
    public void Arrived(ScenePresence sp)
    {
        if (sp.IsNPC) return;
        var (id, session, agent, region) = (Guid.NewGuid(), sp.ControllingClient.SessionId.Guid, sp.UUID.Guid, sp.Scene.RegionInfo.RegionID.Guid);
        if (Take(agent, l => l.To == region) is { } leaving) Handoff(leaving);
        Enqueue("an arrival", Kind.Arrival, session, ct => _service.ArrivedAsync(id, session, agent, region, ct));
    }

    /// <summary>
    /// A teleport out of <paramref name="fromRegion"/> was accepted by <paramref name="finalDestination"/> (<c>OnTeleportStart</c>).
    /// A teleport within this grid is remembered and handed off once it completes (<see cref="Released"/>); one that leaves
    /// this grid (<paramref name="gridLogout"/>) is handed off at once. A teleport within the region hands nobody over.
    /// </summary>
    public void TeleportStarting(IClientAPI client, Guid fromRegion, GridRegion finalDestination, bool gridLogout)
    {
        if (IsNpc(client) || finalDestination is null) return;
        var to = finalDestination.RegionID.Guid;
        if (to == fromRegion) return;
        var (agent, session) = (client.AgentId.Guid, client.SessionId.Guid);
        if (gridLogout)
        {
            Forget(agent);
            Handoff(session, fromRegion, to, "other_grid");
            return;
        }
        Remember(agent, new Leaving(session, fromRegion, to, "teleport"));
    }

    /// <summary>
    /// A teleport out of <paramref name="fromRegion"/> failed (<c>OnTeleportFail</c>): nobody is handed over, and a handoff
    /// to another grid is taken back.
    /// </summary>
    public void TeleportFailed(IClientAPI client, Guid fromRegion)
    {
        if (IsNpc(client)) return;
        Forget(client.AgentId.Guid);
        var session = client.SessionId.Guid;
        Enqueue("a cancelled handoff", Kind.Other, session, ct => _service.CancelHandoffAsync(session, fromRegion, ct));
    }

    /// <summary>
    /// An avatar is crossing into <paramref name="newRegion"/> (<c>OnCrossAgentToNewRegion</c>): remembered, and handed off
    /// once the crossing completes. A retried crossing raises it again; the latest destination is the one kept.
    /// </summary>
    public void Crossing(ScenePresence sp, GridRegion newRegion)
    {
        if (sp.IsNPC || newRegion is null) return;
        Remember(sp.UUID.Guid, new Leaving(sp.ControllingClient.SessionId.Guid, sp.Scene.RegionInfo.RegionID.Guid, newRegion.RegionID.Guid, "crossing"));
    }

    /// <summary>
    /// The region the avatar was in let it go (<c>OnMakeChildAgent</c>), which OpenSim does only once a teleport or crossing
    /// has succeeded: the teleport or crossing remembered from that region is handed off, once. Nothing is sent when none is
    /// remembered.
    /// </summary>
    public void Released(ScenePresence sp)
    {
        if (sp.IsNPC) return;
        if (Take(sp.UUID.Guid, l => l.From == sp.Scene.RegionInfo.RegionID.Guid) is { } leaving) Handoff(leaving);
    }

    /// <summary>The regions this program runs, at <see cref="Protocol"/>. Replaces a health report still waiting.</summary>
    public void Health(IReadOnlyList<Guid> regions)
    {
        var copy = regions.ToArray();
        Enqueue("a health report", Kind.Health, Guid.Empty, ct => _service.HealthAsync(Protocol, copy, ct));
    }

    /// <summary>Completes once every report queued so far has been answered or dropped (for tests).</summary>
    public Task FlushAsync()
    {
        lock (_gate) return _idle.Task;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Cancel();
        // A report in flight is cancelled with the loop; give it a moment so the client isn't disposed under it. The loop
        // catches everything, but nothing it did may come out of the module's Close.
        bool ended;
        try
        {
            ended = _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            ended = true;
        }
        if (!ended) return;
        _stop.Dispose();
        lock (_gate) _queued.Dispose();
    }

    private static bool IsNpc(IClientAPI client) => client.SceneAgent is { PresenceType: PresenceType.Npc };

    private static string Reports(int n) => n == 1 ? "1 place report" : $"{n} place reports";

    private void Handoff(Leaving l) => Handoff(l.Session, l.From, l.To, l.Kind);

    private void Handoff(Guid session, Guid from, Guid to, string kind)
    {
        var id = Guid.NewGuid();
        Enqueue(kind == "other_grid" ? "a handoff to another grid" : $"a {kind} handoff", Kind.Handoff, session,
            ct => _service.HandoffAsync(id, session, from, to, kind, ct));
    }

    /// <summary>Remembers what <paramref name="agent"/> is leaving by; a newer one takes the place of the last.</summary>
    private void Remember(Guid agent, Leaving leaving)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_disposed) return;
            // A teleport cancelled without an event, or an avatar that logged out mid-way, leaves an entry behind: past a
            // few hundred, those older than a report's life go.
            if (_leaving.Count >= MaxRemembered)
                foreach (var old in _leaving.Where(l => now - l.Value.At > MaxAge).Select(l => l.Key).ToList())
                    _leaving.Remove(old);
            _leaving[agent] = leaving with { At = now };
        }
    }

    private void Forget(Guid agent)
    {
        lock (_gate) _leaving.Remove(agent);
    }

    /// <summary>Takes what <paramref name="agent"/> is leaving by, when it <paramref name="matches"/>; null otherwise.</summary>
    private Leaving? Take(Guid agent, Func<Leaving, bool> matches)
    {
        lock (_gate)
        {
            if (!_leaving.TryGetValue(agent, out var leaving) || !matches(leaving)) return null;
            _leaving.Remove(agent);
            return leaving;
        }
    }

    private static TaskCompletionSource Idle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult();
        return idle;
    }

    private void Enqueue(string what, Kind kind, Guid session, Func<CancellationToken, Task<MoneyAnswer>> send)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_idle.Task.IsCompleted) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var report = new Report(what, kind, session, _time.GetUtcNow(), send);
            // A newer health report takes the waiting one's place in the queue: it says the same, more recently.
            if (kind == Kind.Health && Waiting(r => r.Kind == Kind.Health) is { } health)
            {
                health.Value = report;
                return;
            }
            if (_waiting.Count >= MaxWaiting)
            {
                _waiting.RemoveFirst();
                _overflowed++;
            }
            _waiting.AddLast(report);
            // Inside the lock: Dispose, which disposes the semaphore, can't come between the check above and this.
            _queued.Release();
        }
    }

    private LinkedListNode<Report>? Waiting(Func<Report, bool> match)
    {
        for (var node = _waiting.First; node is not null; node = node.Next)
            if (match(node.Value)) return node;
        return null;
    }

    /// <summary>
    /// The report to send next: the oldest, except that waiting arrivals let a handoff of someone else's go first. Each of
    /// those arrivals may be held at the service for up to 10 seconds while its own handoff is on its way. A handoff never
    /// goes ahead of an arrival of its own session, which may be what places the session where the handoff starts.
    /// </summary>
    private LinkedListNode<Report>? Next()
    {
        if (_waiting.First is not { Value.Kind: Kind.Arrival } first) return _waiting.First;
        var arriving = new HashSet<Guid>();
        for (var node = first; node is not null; node = node.Next)
        {
            if (node.Value.Kind == Kind.Handoff && !arriving.Contains(node.Value.Session)) return node;
            if (node.Value.Kind == Kind.Arrival) arriving.Add(node.Value.Session);
        }
        return first;
    }

    private bool TooOld(Report r) => r.Kind != Kind.Health && _time.GetUtcNow() - r.Queued > MaxAge;

    /// <summary>
    /// Runs until the reporter is disposed. Whatever goes wrong with one report is logged by type and the loop goes on, so
    /// the program never silently stops reporting.
    /// </summary>
    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        var failures = new HashSet<string>(StringComparer.Ordinal);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await NextAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Closing: what is still queued is lost, as when the program stops.
            }
            catch (Exception e)
            {
                // Each kind of failure is logged once; a pause keeps one that repeats from spinning.
                if (failures.Add(e.GetType().Name)) Warn($"The place reporter's loop failed with {e.GetType().Name} and goes on.");
                lock (_gate)
                    if (_waiting.Count == 0) _idle.TrySetResult();
                try
                {
                    await Task.Delay(FailurePause, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Closing.
                }
            }
        }
    }

    /// <summary>Drops what is too old, then sends the oldest report left, or waits for one.</summary>
    private async Task NextAsync(CancellationToken ct)
    {
        Report? report;
        int overflowed, stale = 0;
        lock (_gate)
        {
            (overflowed, _overflowed) = (_overflowed, 0);
            for (var node = _waiting.First; node is not null;)
            {
                var next = node.Next;
                if (TooOld(node.Value))
                {
                    _waiting.Remove(node);
                    stale++;
                }
                node = next;
            }
            var chosen = Next();
            report = chosen?.Value;
            if (chosen is not null) _waiting.Remove(chosen);
        }
        if (overflowed > 0) Warn($"Dropped {Reports(overflowed)}: more than {MaxWaiting} were waiting for the money service.");
        if (stale > 0) Warn($"Dropped {Reports(stale)} older than {MaxAge.TotalMinutes:0} minutes, unsent.");
        if (report is null)
        {
            lock (_gate)
                if (_waiting.Count == 0) _idle.TrySetResult();
            await _queued.WaitAsync(ct).ConfigureAwait(false);
            return;
        }
        try
        {
            await SendAsync(report, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
                if (_waiting.Count == 0) _idle.TrySetResult();
        }
    }

    /// <summary>Tells <c>warn</c>; a warning that can't be written is lost rather than stopping the loop.</summary>
    private void Warn(string text)
    {
        try
        {
            _warn(text);
        }
        catch (Exception)
        {
            // Nowhere left to say it.
        }
    }

    private async Task SendAsync(Report report, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await TryAsync(report, ct).ConfigureAwait(false) != MoneyAnswer.UnavailableCode) return;
            if (attempt == Retries.Length)
            {
                Warn($"Dropped {report.What}: the money service couldn't be reached in {attempt + 1} tries.");
                return;
            }
            await Task.Delay(Retries[attempt], _time, ct).ConfigureAwait(false);
            if (TooOld(report))
            {
                Warn($"Dropped {report.What}: older than {MaxAge.TotalMinutes:0} minutes before the money service could be reached.");
                return;
            }
        }
    }

    /// <summary>The answer's code. The service never throws for a failed call; anything else thrown counts as unavailable.</summary>
    private async Task<string> TryAsync(Report report, CancellationToken ct)
    {
        try
        {
            return (await report.Send(ct).ConfigureAwait(false)).Code;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Warn($"Sending {report.What} failed with {e.GetType().Name}.");
            return MoneyAnswer.UnavailableCode;
        }
    }
}
