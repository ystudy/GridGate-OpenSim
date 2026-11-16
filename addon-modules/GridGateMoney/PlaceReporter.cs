using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace GridGate.Region.Money;

/// <summary>
/// Tells the money service where avatars are (money design §8.4, §8.10): arrivals, handoffs (teleports, crossings,
/// leaving the grid), failed teleports and this program's health. Each event only queues its report and returns, so a
/// teleport or crossing never waits for the service. One loop sends them one at a time, oldest first: the program never
/// has more than one call open, and a handoff from one of its regions always reaches the service before the arrival it
/// vouches for in another. A send answered "unavailable" is tried again after 1, 3, 9 and 17 seconds with the same id,
/// then dropped; any other answer is final. NPCs are never reported.
/// </summary>
internal sealed class PlaceReporter : IDisposable
{
    /// <summary>The money handoff protocol this module speaks (money design §8.2).</summary>
    public const int Protocol = 2;

    private static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(17)];

    private readonly IMoneyService _service;
    private readonly TimeProvider _time;
    private readonly Action<string> _warn;
    private readonly Channel<Report> _queue = Channel.CreateUnbounded<Report>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private int _pending;
    private bool _disposed;
    private TaskCompletionSource _idle = Idle();

    /// <summary>One report: what it is, for the log (never who it is about), and how to send it, with its id fixed.</summary>
    private sealed record Report(string What, Func<CancellationToken, Task<MoneyAnswer>> Send);

    /// <param name="warn">Told when a report is dropped; never with a session, an avatar or an id.</param>
    public PlaceReporter(IMoneyService service, TimeProvider time, Action<string> warn)
    {
        (_service, _time, _warn) = (service, time, warn);
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>An avatar became a root agent in its region (<c>OnMakeRootAgent</c>).</summary>
    public void Arrived(ScenePresence sp)
    {
        if (sp.IsNPC) return;
        var (id, session, agent, region) = (Guid.NewGuid(), sp.ControllingClient.SessionId.Guid, sp.UUID.Guid, sp.Scene.RegionInfo.RegionID.Guid);
        Enqueue("an arrival", ct => _service.ArrivedAsync(id, session, agent, region, ct));
    }

    /// <summary>
    /// A teleport out of <paramref name="fromRegion"/> was accepted by <paramref name="finalDestination"/> (<c>OnTeleportStart</c>);
    /// <paramref name="gridLogout"/> means it leaves this grid. A teleport within the region hands nobody over.
    /// </summary>
    public void TeleportStarting(IClientAPI client, Guid fromRegion, GridRegion finalDestination, bool gridLogout)
    {
        if (IsNpc(client) || finalDestination is null) return;
        var to = finalDestination.RegionID.Guid;
        if (to == fromRegion) return;
        Handoff(client.SessionId.Guid, fromRegion, to, gridLogout ? "other_grid" : "teleport");
    }

    /// <summary>A teleport out of <paramref name="fromRegion"/> failed (<c>OnTeleportFail</c>): its handoff is void.</summary>
    public void TeleportFailed(IClientAPI client, Guid fromRegion)
    {
        if (IsNpc(client)) return;
        var session = client.SessionId.Guid;
        Enqueue("a cancelled handoff", ct => _service.CancelHandoffAsync(session, fromRegion, ct));
    }

    /// <summary>
    /// An avatar is crossing into <paramref name="newRegion"/> (<c>OnCrossAgentToNewRegion</c>, before the destination is
    /// told). A retried crossing raises it again: that is another handoff, which replaces the open one at the service.
    /// </summary>
    public void Crossing(ScenePresence sp, GridRegion newRegion)
    {
        if (sp.IsNPC || newRegion is null) return;
        Handoff(sp.ControllingClient.SessionId.Guid, sp.Scene.RegionInfo.RegionID.Guid, newRegion.RegionID.Guid, "crossing");
    }

    /// <summary>The regions this program runs, at <see cref="Protocol"/> (money design §8.2).</summary>
    public void Health(IReadOnlyList<Guid> regions)
    {
        var copy = regions.ToArray();
        Enqueue("a health report", ct => _service.HealthAsync(Protocol, copy, ct));
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
        _queue.Writer.TryComplete();
        _stop.Cancel();
        // A report in flight is cancelled with the loop; give it a moment so the client isn't disposed under it.
        if (_loop.Wait(TimeSpan.FromSeconds(2))) _stop.Dispose();
    }

    private static bool IsNpc(IClientAPI client) => client.SceneAgent is { PresenceType: PresenceType.Npc };

    private void Handoff(Guid session, Guid from, Guid to, string kind)
    {
        var id = Guid.NewGuid();
        Enqueue(kind == "other_grid" ? "a handoff to another grid" : $"a {kind} handoff", ct => _service.HandoffAsync(id, session, from, to, kind, ct));
    }

    private static TaskCompletionSource Idle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult();
        return idle;
    }

    private void Enqueue(string what, Func<CancellationToken, Task<MoneyAnswer>> send)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (!_queue.Writer.TryWrite(new Report(what, send))) Finished();
    }

    private void Finished()
    {
        lock (_gate)
            if (--_pending == 0) _idle.TrySetResult();
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            await foreach (var report in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(report, ct).ConfigureAwait(false);
                }
                finally
                {
                    Finished();
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Closing: what is still queued is lost, as when the program stops.
        }
    }

    private async Task SendAsync(Report report, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (await TryAsync(report, ct).ConfigureAwait(false) != MoneyAnswer.UnavailableCode) return;
            if (attempt == Retries.Length)
            {
                _warn($"Dropped {report.What}: the money service couldn't be reached in {attempt + 1} tries.");
                return;
            }
            await Task.Delay(Retries[attempt], _time, ct).ConfigureAwait(false);
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
            _warn($"Sending {report.What} failed with {e.GetType().Name}.");
            return MoneyAnswer.UnavailableCode;
        }
    }
}
