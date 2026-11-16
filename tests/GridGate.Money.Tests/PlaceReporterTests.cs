using System.Diagnostics;
using GridGate.Region.Money;
using Microsoft.Extensions.Time.Testing;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using static GridGate.Money.Tests.MoneyScene;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace GridGate.Money.Tests;

/// <summary>
/// The region module tells the money service where avatars go: arrivals, teleport and crossing
/// handoffs, leaving the grid, failed teleports and the program's health. It never makes a teleport wait for the service.
/// </summary>
[Collection(SceneCollection.Name)]
public sealed class PlaceReporterTests
{
    private static readonly UUID Region = TestHelpers.ParseTail(0x100);
    private static readonly GridRegion Elsewhere = new() { RegionID = TestHelpers.ParseTail(0x300), RegionName = "Elsewhere" };

    private static void TeleportStart(MoneyScene region, IClientAPI client, GridRegion to, bool gridLogout = false) =>
        region.Scene.EventManager.TriggerTeleportStart(client, to, to, (uint)TeleportFlags.ViaLandmark, gridLogout);

    /// <summary>The place reports other than health, in the order the service got them.</summary>
    private static List<Call> Moves(FakeMoneyService service) => service.Places.Where(c => c.Method != "health").ToList();

    /// <summary>A region coming up says at once which regions this program runs, at protocol 2.</summary>
    [Fact]
    public void Health_is_sent_when_a_region_is_added()
    {
        var region = Build();

        region.FlushPlaces();

        var health = Assert.Single(region.Service.PlacesOf("health"));
        Assert.Equal(2, health.Args[0]);
        Assert.Equal(new[] { Region.Guid }, (Guid[])health.Args[1]!);
    }

    /// <summary>Becoming a root agent is one arrival, with the avatar's session, id and region; a child agent is none.</summary>
    [Fact]
    public void Arrival_is_reported_once_per_root()
    {
        var region = Build();
        SceneHelpers.AddChildScenePresence(region.Scene, Stranger);
        region.FlushPlaces();
        Assert.Empty(region.Service.PlacesOf("arrived"));

        var client = region.Arrive(Buyer);
        region.FlushPlaces();

        var arrived = Assert.Single(region.Service.PlacesOf("arrived"));
        Assert.NotEqual(Guid.Empty, arrived.Txn);
        Assert.Equal(new object?[] { client.SessionId.Guid, Buyer.Guid, Region.Guid }, arrived.Args);
    }

    /// <summary>NPCs are never placed: their arrivals, teleports and crossings report nothing.</summary>
    [Fact]
    public void Npc_is_never_reported()
    {
        var region = Build();
        var npcId = TestHelpers.ParseTail(0x400);
        var data = SceneHelpers.GenerateAgentData(npcId);
        var npc = new TestClient(data, region.Scene);
        // As OpenSim's NPC module brings one in.
        region.Scene.AuthenticateHandler.AddNewCircuit(data);
        region.Scene.AddNewAgent(npc, PresenceType.Npc);
        var sp = region.Scene.GetScenePresence(npcId);
        sp.CompleteMovement(npc, false);
        Assert.True(sp.IsNPC);
        Assert.False(sp.IsChildAgent);

        TeleportStart(region, npc, Elsewhere);
        region.Scene.EventManager.TriggerTeleportFail(npc, false);
        region.Scene.EventManager.TriggerCrossAgentToNewRegion(sp, false, Elsewhere);
        region.FlushPlaces();
        Assert.Empty(Moves(region.Service));

        // The same events for a person are reported: the NPC's silence isn't the module's.
        region.Arrive(Buyer);
        region.FlushPlaces();
        Assert.Equal(Buyer.Guid, Assert.Single(Moves(region.Service)).Args[1]);
    }

    /// <summary>A teleport within the region hands nobody over.</summary>
    [Fact]
    public void Same_region_teleport_reports_nothing()
    {
        var region = Build();
        var client = region.Arrive(Buyer);
        region.FlushPlaces();

        TeleportStart(region, client, new GridRegion(region.Scene.RegionInfo));
        region.FlushPlaces();

        Assert.Empty(region.Service.PlacesOf("handoff"));
    }

    /// <summary>A teleport's start hands nobody over yet: the handoff goes when this region lets the avatar go.</summary>
    [Fact]
    public void Teleport_hands_off_when_the_avatar_is_let_go()
    {
        var region = Build();
        var client = region.Arrive(Buyer);
        var sp = region.Scene.GetScenePresence(Buyer);

        TeleportStart(region, client, Elsewhere);
        region.FlushPlaces();
        Assert.Empty(region.Service.PlacesOf("handoff"));
        region.Scene.EventManager.TriggerOnMakeChildAgent(sp);
        region.FlushPlaces();

        var handoff = Assert.Single(region.Service.PlacesOf("handoff"));
        Assert.NotEqual(Guid.Empty, handoff.Txn);
        Assert.Equal(new object?[] { client.SessionId.Guid, Region.Guid, Elsewhere.RegionID.Guid, "teleport" }, handoff.Args);
        Assert.Equal(new[] { "arrived", "handoff" }, Moves(region.Service).Select(c => c.Method));

        // Sent once: letting the avatar go again sends nothing more.
        region.Scene.EventManager.TriggerOnMakeChildAgent(sp);
        region.FlushPlaces();
        Assert.Single(region.Service.PlacesOf("handoff"));
    }

    /// <summary>Being let go with no teleport or crossing remembered hands nobody over.</summary>
    [Fact]
    public void Letting_go_with_nothing_remembered_sends_nothing()
    {
        var region = Build();
        region.Arrive(Buyer);

        region.Scene.EventManager.TriggerOnMakeChildAgent(region.Scene.GetScenePresence(Buyer));
        region.FlushPlaces();

        Assert.Equal(new[] { "arrived" }, Moves(region.Service).Select(c => c.Method));
    }

    /// <summary>A teleport to another grid is "left the grid", at once; letting the avatar go afterwards sends nothing more.</summary>
    [Fact]
    public void Leaving_the_grid_sends_other_grid()
    {
        var region = Build();
        var client = region.Arrive(Buyer);

        TeleportStart(region, client, Elsewhere, gridLogout: true);
        region.FlushPlaces();
        region.Scene.EventManager.TriggerOnMakeChildAgent(region.Scene.GetScenePresence(Buyer));
        region.FlushPlaces();

        var handoff = Assert.Single(region.Service.PlacesOf("handoff"));
        Assert.Equal(new object?[] { client.SessionId.Guid, Region.Guid, Elsewhere.RegionID.Guid, "other_grid" }, handoff.Args);
    }

    /// <summary>A failed teleport sends its cancel and forgets the destination: nobody is handed over.</summary>
    [Fact]
    public void Teleport_fail_cancels_and_hands_nobody_over()
    {
        var region = Build();
        var client = region.Arrive(Buyer);
        TeleportStart(region, client, Elsewhere);

        region.Scene.EventManager.TriggerTeleportFail(client, false);
        region.Scene.EventManager.TriggerOnMakeChildAgent(region.Scene.GetScenePresence(Buyer));
        region.FlushPlaces();

        var cancel = Assert.Single(region.Service.PlacesOf("cancel"));
        Assert.Equal(new object?[] { client.SessionId.Guid, Region.Guid }, cancel.Args);
        Assert.Equal(new[] { "arrived", "cancel" }, Moves(region.Service).Select(c => c.Method));
    }

    /// <summary>A crossing that fails never lets the avatar go, so it hands nobody over.</summary>
    [Fact]
    public void A_failed_crossing_sends_nothing()
    {
        var region = Build();
        region.Arrive(Buyer);

        region.Scene.EventManager.TriggerCrossAgentToNewRegion(region.Scene.GetScenePresence(Buyer), false, Elsewhere);
        region.FlushPlaces();

        Assert.Empty(region.Service.PlacesOf("handoff"));
    }

    /// <summary>A retried crossing can raise the event again: the latest destination is the one handed off to, once.</summary>
    [Fact]
    public void A_repeated_crossing_is_one_handoff()
    {
        var region = Build();
        region.Arrive(Buyer);
        var sp = region.Scene.GetScenePresence(Buyer);
        var next = new GridRegion { RegionID = TestHelpers.ParseTail(0x301), RegionName = "Next" };

        region.Scene.EventManager.TriggerCrossAgentToNewRegion(sp, false, Elsewhere);
        region.Scene.EventManager.TriggerCrossAgentToNewRegion(sp, false, next);
        region.Scene.EventManager.TriggerOnMakeChildAgent(sp);
        region.FlushPlaces();

        var handoff = Assert.Single(region.Service.PlacesOf("handoff"));
        Assert.Equal(new object?[] { sp.ControllingClient.SessionId.Guid, Region.Guid, next.RegionID.Guid, "crossing" }, handoff.Args);
    }

    /// <summary>
    /// A send the service answers "unavailable" is tried again after 1, 3, 9 and 17 seconds with the same
    /// id, then dropped with one warning that names nobody.
    /// </summary>
    [Fact]
    public async Task Unavailable_is_retried_with_the_same_id_then_dropped()
    {
        var region = Build();
        var client = region.Arrive(Buyer);
        var sp = region.Scene.GetScenePresence(Buyer);
        var service = new FakeMoneyService { Answer = _ => MoneyAnswer.Unavailable };
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var at = new List<DateTimeOffset>();
        service.Gate = _ =>
        {
            lock (at) at.Add(time.GetUtcNow());
            return Task.CompletedTask;
        };
        var warnings = new List<string>();
        using var places = new PlaceReporter(service, time, w => { lock (warnings) warnings.Add(w); });
        int Tries() { lock (at) return at.Count; }

        places.Arrived(sp);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Tries() < 1) { Assert.True(DateTime.UtcNow < deadline, "the arrival was never sent"); Thread.Sleep(1); }
        // Moved on in small steps, so each retry is sent as soon as its wait is over.
        while (Tries() < 5)
        {
            Assert.True(DateTime.UtcNow < deadline, $"only {Tries()} tries were made");
            time.Advance(TimeSpan.FromMilliseconds(100));
            Thread.Sleep(1);
        }
        await places.FlushAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var sent = service.Places.ToList();
        Assert.Equal(5, sent.Count);
        Assert.Single(sent.Select(c => c.Txn).Distinct());
        Assert.NotEqual(Guid.Empty, sent[0].Txn);
        lock (at)
        {
            var waits = at.Zip(at.Skip(1), (a, b) => b - a).ToList();
            var expected = new[] { 1, 3, 9, 17 };
            for (var i = 0; i < expected.Length; i++)
                Assert.InRange(waits[i], TimeSpan.FromSeconds(expected[i]), TimeSpan.FromSeconds(expected[i] + 0.5));
        }
        var warning = Assert.Single(warnings);
        Assert.DoesNotContain(client.SessionId.ToString(), warning);
        Assert.DoesNotContain(Buyer.ToString(), warning);
        Assert.DoesNotContain(sent[0].Txn.ToString(), warning);
    }

    /// <summary>
    /// OpenSim adds a region before registering it with the grid, so a Region Server's key doesn't
    /// cover it at that first report. Once the region has started it is reported again, with the region in it.
    /// </summary>
    [Fact]
    public void Health_is_sent_again_when_the_region_has_started()
    {
        var region = Build();
        region.FlushPlaces();
        var before = region.Service.PlacesOf("health").Count();

        region.Scene.EventManager.TriggerOnRegionStarted(region.Scene);
        region.FlushPlaces();

        var health = region.Service.PlacesOf("health").ToList();
        Assert.Equal(before + 1, health.Count);
        Assert.Equal(2, health[^1].Args[0]);
        Assert.Equal(new[] { Region.Guid }, (Guid[])health[^1].Args[1]!);
    }

    /// <summary>Removing a region reports health at once without it, and its start no longer reports.</summary>
    [Fact]
    public void Health_is_sent_without_a_removed_region()
    {
        var region = Build();
        region.FlushPlaces();

        region.Module.RemoveRegion(region.Scene);
        region.FlushPlaces();
        var count = region.Service.PlacesOf("health").Count();
        region.Scene.EventManager.TriggerOnRegionStarted(region.Scene);
        region.FlushPlaces();

        var health = region.Service.PlacesOf("health").ToList();
        Assert.Empty((Guid[])health[^1].Args[1]!);
        Assert.Equal(count, health.Count);
    }

    /// <summary>A time provider whose timers can't be made: something going wrong inside the reporter's loop, on demand.</summary>
    private sealed class NoTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("no timers here");
    }

    /// <summary>
    /// Review M2: a failure inside the loop (here, waiting to retry) is logged by its type and the loop goes on, so the
    /// program never silently stops reporting.
    /// </summary>
    [Fact]
    public async Task The_loop_goes_on_after_a_failure()
    {
        var region = Build();
        var client = region.Arrive(Buyer);
        var sp = region.Scene.GetScenePresence(Buyer);
        var service = new FakeMoneyService();
        service.Queue("handoff", MoneyAnswer.Unavailable, MoneyAnswer.Unavailable);
        var warnings = new List<string>();
        using var places = new PlaceReporter(service, new NoTimers(), w => { lock (warnings) warnings.Add(w); });

        foreach (var n in new[] { 1, 2, 3 })
        {
            places.TeleportStarting(client, Region.Guid, HeldReporter.To(n), false);
            places.Released(sp);
            await places.FlushAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, service.PlacesOf("handoff").Count());
        Assert.Equal(HeldReporter.To(3).RegionID.Guid, service.PlacesOf("handoff").Last().Args[2]);
        // The same failure twice is logged once.
        var warning = Assert.Single(warnings);
        Assert.Contains(nameof(InvalidOperationException), warning);
        Assert.DoesNotContain("no timers here", warning);
    }

    /// <summary>Review M1: once disposed, events and health queue nothing and throw nothing, and disposing again is harmless.</summary>
    [Fact]
    public void Reports_after_dispose_are_ignored()
    {
        var region = Build();
        var client = region.Arrive(Buyer);
        var service = new FakeMoneyService();
        var places = new PlaceReporter(service, TimeProvider.System, _ => { });

        places.Dispose();
        places.TeleportStarting(client, Region.Guid, Elsewhere, false);
        places.Health([Region.Guid]);
        places.Dispose();

        Assert.True(places.FlushAsync().IsCompleted);
        Assert.Empty(service.Places);
    }

    /// <summary>
    /// A reporter on a fake clock whose service holds the first report it is sent (a teleport handoff to region 0) until
    /// released, so the reports queued after it wait.
    /// </summary>
    private sealed class HeldReporter : IDisposable
    {
        private static readonly Dictionary<Guid, int> Numbers = Enumerable.Range(0, PlaceReporter.MaxWaiting + 10).ToDictionary(n => To(n).RegionID.Guid);
        private readonly TaskCompletionSource _release = new();
        private int _calls;

        public FakeMoneyService Service { get; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
        public List<string> Warnings { get; } = [];
        public PlaceReporter Places { get; }
        public RecordingClient Client { get; }
        public ScenePresence Avatar { get; }

        /// <summary>The test region the avatars are in; its own module reports to its own fake service.</summary>
        public MoneyScene Here { get; }

        public HeldReporter()
        {
            Here = Build();
            Client = Here.Arrive(Buyer);
            Avatar = Here.Scene.GetScenePresence(Buyer);
            Service.Gate = _ => Interlocked.Increment(ref _calls) == 1 ? _release.Task : Task.CompletedTask;
            Places = new PlaceReporter(Service, Time, w => { lock (Warnings) Warnings.Add(w); });
            Teleport(0);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Service.Places.IsEmpty)
            {
                Assert.True(DateTime.UtcNow < deadline, "the first report was never sent");
                Thread.Sleep(1);
            }
        }

        public static GridRegion To(int n) => new() { RegionID = TestHelpers.ParseTail(0x10000 + n), RegionName = "R" + n };

        /// <summary>Queues a teleport handoff from the test region to region <paramref name="n"/>.</summary>
        public void Teleport(int n)
        {
            Places.TeleportStarting(Client, Region.Guid, To(n), false);
            Places.Released(Avatar);
        }

        /// <summary>Lets the first report through and waits until everything queued is answered or dropped.</summary>
        public async Task ReleaseAndFlushAsync()
        {
            _release.TrySetResult();
            await Places.FlushAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }

        /// <summary>The numbers of the regions the service was told of handoffs to, in order.</summary>
        public List<int> HandedTo() => Service.PlacesOf("handoff").Select(c => Numbers[(Guid)c.Args[2]!]).ToList();

        public void Dispose()
        {
            _release.TrySetResult();
            Places.Dispose();
        }
    }

    /// <summary>
    /// A handoff or arrival older than the service's 2-minute handoff life is dropped unsent, with
    /// one warning that counts them and names nobody; a newer one still goes.
    /// </summary>
    [Fact]
    public async Task Reports_older_than_two_minutes_are_dropped_unsent()
    {
        using var held = new HeldReporter();
        held.Teleport(1);
        held.Places.Arrived(held.Avatar);
        held.Time.Advance(PlaceReporter.MaxAge + TimeSpan.FromSeconds(1));
        held.Teleport(2);

        await held.ReleaseAndFlushAsync();

        Assert.Equal(new[] { 0, 2 }, held.HandedTo());
        Assert.Empty(held.Service.PlacesOf("arrived"));
        var warning = Assert.Single(held.Warnings);
        Assert.Contains("2 place reports", warning);
        Assert.DoesNotContain(held.Client.SessionId.ToString(), warning);
        Assert.DoesNotContain(Buyer.ToString(), warning);
    }

    /// <summary>At most one health report waits; the newest takes its place, so an outage can't stack them.</summary>
    [Fact]
    public async Task Health_reports_waiting_are_coalesced()
    {
        using var held = new HeldReporter();
        held.Places.Health([TestHelpers.ParseTail(0x501).Guid]);
        held.Teleport(1);
        held.Places.Health([TestHelpers.ParseTail(0x502).Guid]);
        held.Places.Health([TestHelpers.ParseTail(0x503).Guid]);

        await held.ReleaseAndFlushAsync();

        var health = Assert.Single(held.Service.PlacesOf("health"));
        Assert.Equal(new[] { TestHelpers.ParseTail(0x503).Guid }, (Guid[])health.Args[1]!);
        // It kept the first one's place in the queue, before the handoff queued after it.
        Assert.Equal(new[] { "handoff", "health", "handoff" }, held.Service.Places.Select(c => c.Method));
        Assert.Empty(held.Warnings);
    }

    /// <summary>Past 1,000 waiting reports the oldest go, with one warning that counts them.</summary>
    [Fact]
    public async Task The_queue_cap_drops_the_oldest()
    {
        using var held = new HeldReporter();
        for (var n = 1; n <= PlaceReporter.MaxWaiting + 2; n++) held.Teleport(n);

        await held.ReleaseAndFlushAsync();

        Assert.Equal(new[] { 0 }.Concat(Enumerable.Range(3, PlaceReporter.MaxWaiting)), held.HandedTo());
        var warning = Assert.Single(held.Warnings);
        Assert.Contains("2 place reports", warning);
        Assert.Contains("1000", warning);
    }

    /// <summary>
    /// A waiting handoff goes ahead of other people's waiting arrivals, which the service may hold for their own handoff
    /// for up to 10 seconds; the rest go oldest first.
    /// </summary>
    [Fact]
    public async Task Waiting_handoffs_go_ahead_of_other_peoples_arrivals()
    {
        using var held = new HeldReporter();
        held.Here.Arrive(Stranger);
        var stranger = held.Here.Scene.GetScenePresence(Stranger);
        held.Places.Arrived(stranger);
        held.Places.Health([Region.Guid]);
        held.Teleport(1);
        held.Places.Arrived(stranger);

        await held.ReleaseAndFlushAsync();

        Assert.Equal(new[] { "handoff", "handoff", "arrived", "health", "arrived" }, held.Service.Places.Select(c => c.Method));
        Assert.Equal(new[] { 0, 1 }, held.HandedTo());
    }

    /// <summary>A handoff never goes ahead of an arrival of the same session queued before it: that arrival may be what places them where the handoff starts.</summary>
    [Fact]
    public async Task A_handoff_waits_behind_its_own_sessions_arrival()
    {
        using var held = new HeldReporter();
        held.Places.Arrived(held.Avatar);
        held.Teleport(1);

        await held.ReleaseAndFlushAsync();

        Assert.Equal(new[] { "handoff", "arrived", "handoff" }, held.Service.Places.Select(c => c.Method));
    }

    /// <summary>
    /// No slower teleports: letting the avatar go returns at once while the service holds an earlier report, and reports go
    /// out one at a time.
    /// </summary>
    [Fact]
    public void A_teleport_never_waits_for_the_service()
    {
        var region = Build();
        var client = region.Arrive(Buyer);
        var sp = region.Scene.GetScenePresence(Buyer);
        region.FlushPlaces();
        var release = new TaskCompletionSource();
        region.Service.Gate = m => m == "handoff" ? release.Task : Task.CompletedTask;
        try
        {
            TeleportStart(region, client, Elsewhere);
            region.Scene.EventManager.TriggerOnMakeChildAgent(sp);
            region.WaitForPlace("handoff");

            TeleportStart(region, client, Elsewhere);
            var clock = Stopwatch.StartNew();
            region.Scene.EventManager.TriggerOnMakeChildAgent(sp);
            clock.Stop();

            Assert.True(clock.ElapsedMilliseconds < 50, $"the event took {clock.ElapsedMilliseconds} ms");
            Thread.Sleep(100);
            Assert.Single(region.Service.PlacesOf("handoff"));
        }
        finally
        {
            release.TrySetResult();
        }
        region.FlushPlaces();
        Assert.Equal(2, region.Service.PlacesOf("handoff").Count());
    }
}
