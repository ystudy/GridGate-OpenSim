using GridGate.Region.Money;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Framework;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;
using static GridGate.Money.Tests.MoneyScene;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace GridGate.Money.Tests;

/// <summary>
/// When the region module hands an avatar on, through OpenSim's own teleports and crossings between two test regions. A
/// handoff goes out only once the region being left has let the avatar go: a teleport or crossing that fails hands nobody
/// over. When both regions are this program's, the handoff goes out as soon as the avatar is in the destination, ahead of
/// the arrival there, since one queue sends one report at a time. Built like upstream's
/// <c>ScenePresenceTeleportTests</c> and <c>ScenePresenceCrossingTests</c>.
/// </summary>
[Collection(SceneCollection.Name)]
public sealed class HandoffTimingTests
{
    private static readonly UUID UserId = TestHelpers.ParseTail(0x1);

    /// <summary>An entity transfer module whose UpdateAgent always fails: the destination took the agent, but the viewer never got there.</summary>
    private sealed class FailingUpdate : EntityTransferModule
    {
        protected override bool UpdateAgent(GridRegion reg, GridRegion finalDestination, AgentData agent, ScenePresence sp, EntityTransferContext ctx) => false;
    }

    /// <summary>Two test regions A and B in one simulator, with the money module in A, and in B too when <paramref name="moneyInB"/>.</summary>
    private sealed class TwoRegions : IDisposable
    {
        private readonly BaseHttpServer? _previousServer = MainServer.Instance;

        public TestScene A { get; }
        public TestScene B { get; }
        public FakeMoneyService Service { get; } = new();
        public GridGateMoneyModule Money { get; }

        public TwoRegions(uint bX, uint bY, bool moneyInB = true, EntityTransferModule? transferA = null)
        {
            Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
            // CapabilitiesModule needs a main HTTP server (a static); put back whatever was there.
            var httpServer = new BaseHttpServer(99999);
            MainServer.AddHttpServer(httpServer);
            MainServer.Instance = httpServer;
            var etmA = transferA ?? new EntityTransferModule();
            var etmB = new EntityTransferModule();
            var lscm = new LocalSimulationConnectorModule();
            IConfigSource config = new IniConfigSource();
            var modules = config.AddConfig("Modules");
            modules.Set("EntityTransferModule", etmA.Name);
            modules.Set("SimulationServices", lscm.Name);

            var sh = new SceneHelpers();
            A = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), 1000, 1000);
            B = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), bX, bY);
            SceneHelpers.SetupSceneModules(new Scene[] { A, B }, config, lscm);
            SceneHelpers.SetupSceneModules(A, config, new CapabilitiesModule(), etmA);
            SceneHelpers.SetupSceneModules(B, config, new CapabilitiesModule(), etmB);
            Money = NewModule(Service);
            AddTo(Money, A);
            if (moneyInB) AddTo(Money, B);
        }

        public Guid IdA => A.RegionInfo.RegionID.Guid;
        public Guid IdB => B.RegionInfo.RegionID.Guid;

        /// <summary>Where region B is reached: its ExternalHostName and UDP port, as Robust would list it.</summary>
        public string HostB => B.RegionInfo.ExternalHostName;
        public int PortB => B.RegionInfo.InternalEndPoint.Port;

        /// <summary>The place reports other than health, in the order the service got them, once all are sent.</summary>
        public List<Call> Moves()
        {
            Flush(Money);
            return Service.Places.Where(c => c.Method != "health").ToList();
        }

        public void Dispose()
        {
            A.Close();
            B.Close();
            if (_previousServer is not null) MainServer.Instance = _previousServer;
            MainServer.RemoveHttpServer(99999);
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        }
    }

    /// <summary>
    /// The avatar, root in A and seeing into B (a child agent there). When <paramref name="viewerReachesB"/>, the viewer
    /// connects to B when told to teleport, as upstream's V2 teleport test sets it up.
    /// </summary>
    private static ScenePresence InA(TwoRegions two, bool viewerReachesB)
    {
        var acd = SceneHelpers.GenerateAgentData(UserId);
        var tc = new TestClient(acd, two.A);
        var inB = new List<TestClient>();
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, inB);
        var sp = SceneHelpers.AddScenePresence(two.A, tc, acd);
        two.A.Update(4);
        two.B.Update(4);
        Assert.True(two.B.GetScenePresence(UserId)!.IsChildAgent);
        if (viewerReachesB)
            tc.OnTestClientSendRegionTeleport += (_, _, _, _, _, _) => ThreadPool.UnsafeQueueUserWorkItem(_ => inB[0].CompleteMovement(), null);
        return sp;
    }

    private static void Teleport(TwoRegions two, ScenePresence sp)
    {
        two.A.RequestTeleportLocation(sp.ControllingClient, two.B.RegionInfo.RegionHandle, new Vector3(10, 11, 12), new Vector3(20, 21, 22),
            (uint)TeleportFlags.ViaLocation);
        two.A.Update(4);
        two.B.Update(4);
    }

    [Fact]
    public void Teleport_between_this_programs_regions_hands_off_ahead_of_the_arrival()
    {
        using var two = new TwoRegions(1001, 1000);
        var sp = InA(two, viewerReachesB: true);
        var session = sp.ControllingClient.SessionId.Guid;

        Teleport(two, sp);

        Assert.True(two.A.GetScenePresence(UserId)!.IsChildAgent);
        Assert.False(two.B.GetScenePresence(UserId)!.IsChildAgent);
        // The arrival in A, one teleport handoff, then the arrival in B: the service always sees the handoff first. A's
        // letting the avatar go afterwards sends nothing more.
        var moves = two.Moves();
        Assert.Equal(new[] { "arrived", "handoff", "arrived" }, moves.Select(c => c.Method));
        // With the address and UDP port the avatar was sent to, as OpenSim found the destination.
        Assert.Equal(new object?[] { session, two.IdA, two.IdB, "teleport", two.HostB, two.PortB }, moves[1].Args);
        Assert.Equal(new object?[] { session, UserId.Guid, two.IdB }, moves[2].Args);
    }

    [Fact]
    public void Crossing_between_this_programs_regions_hands_off_once_ahead_of_the_arrival()
    {
        using var two = new TwoRegions(1000, 999);
        var acd = SceneHelpers.GenerateAgentData(UserId);
        var tc = new TestClient(acd, two.A);
        EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, []);
        var sp = SceneHelpers.AddScenePresence(two.A, tc, acd);
        sp.AbsolutePosition = new Vector3(128, 32, 10);
        sp.HandleAgentUpdate(sp.ControllingClient, new AgentUpdateArgs
        {
            BodyRotation = Quaternion.CreateFromEulers(new Vector3(0, 0, (float)-(Math.PI / 2))),
            ControlFlags = (uint)(AgentManager.ControlFlags.AGENT_CONTROL_AT_POS | AgentManager.ControlFlags.AGENT_CONTROL_FAST_AT),
        });
        // Enough updates for the avatar to walk to the northern border. OpenSim sometimes raises the crossing event more
        // than once for one crossing; the module keeps the latest and still hands off once.
        for (var i = 0; i < 101; i++) two.A.Update(1);
        Assert.True(two.A.GetScenePresence(UserId)!.IsChildAgent);
        ((TestClient)two.B.GetScenePresence(UserId)!.ControllingClient).CompleteMovement();

        var moves = two.Moves();
        Assert.Equal(new[] { "arrived", "handoff", "arrived" }, moves.Select(c => c.Method));
        // With the address and UDP port the avatar crossed to, as OpenSim found the region next door.
        Assert.Equal(new object?[] { tc.SessionId.Guid, two.IdA, two.IdB, "crossing", two.HostB, two.PortB }, moves[1].Args);
        Assert.Equal(two.IdB, moves[2].Args[2]);
    }

    [Fact]
    public void Failed_teleport_between_this_programs_regions_hands_nobody_over()
    {
        using var two = new TwoRegions(1001, 1000, transferA: new FailingUpdate());
        var sp = InA(two, viewerReachesB: false);

        Teleport(two, sp);

        Assert.False(two.A.GetScenePresence(UserId)!.IsChildAgent);
        var moves = two.Moves();
        Assert.DoesNotContain(moves, c => c.Method == "handoff");
        // Only the cancel a failed teleport always sends follows the arrival in A.
        Assert.Equal(new[] { "arrived", "cancel" }, moves.Select(c => c.Method));
    }

    [Fact]
    public void Teleport_to_a_region_outside_this_program_hands_off_when_the_avatar_is_let_go()
    {
        using var two = new TwoRegions(1001, 1000, moneyInB: false);
        var sp = InA(two, viewerReachesB: true);
        var session = sp.ControllingClient.SessionId.Guid;
        var order = new List<string>();
        var handoffsAtRoot = -1;
        two.B.EventManager.OnMakeRootAgent += _ =>
        {
            Flush(two.Money);
            handoffsAtRoot = two.Service.PlacesOf("handoff").Count();
            lock (order) order.Add("root-in-B");
        };
        two.A.EventManager.OnMakeChildAgent += _ => { lock (order) order.Add("child-in-A"); };

        Teleport(two, sp);

        lock (order) Assert.Equal(new[] { "root-in-B", "child-in-A" }, order);
        Assert.Equal(0, handoffsAtRoot); // B isn't this program's: only A letting the avatar go hands it off
        var moves = two.Moves();
        Assert.Equal(new[] { "arrived", "handoff" }, moves.Select(c => c.Method));
        // With the address and UDP port the avatar was sent to, as OpenSim found the destination.
        Assert.Equal(new object?[] { session, two.IdA, two.IdB, "teleport", two.HostB, two.PortB }, moves[1].Args);
    }
}
