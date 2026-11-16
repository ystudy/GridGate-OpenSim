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

namespace GridGate.Money.Tests;

/// <summary>
/// The region money module needs to know, before the destination is told, that an avatar is being handed to the next
/// region. OpenSim declares <c>OnCrossAgentToNewRegion</c> for that but never fired it; GridGate fires it.
/// Built like upstream's <c>ScenePresenceCrossingTests.TestCrossOnSameSimulator</c>.
/// </summary>
[Collection(SceneCollection.Name)]
public sealed class CrossingEventTests
{
    [Fact]
    public void Crossing_fires_OnCrossAgentToNewRegion_once_with_the_destination()
    {
        // Run everything on one thread, as upstream's crossing tests do.
        Util.FireAndForgetMethod = FireAndForgetMethod.RegressionTest;
        // CapabilitiesModule needs a main HTTP server (a static); put back whatever was there.
        var previousServer = MainServer.Instance;
        var httpServer = new BaseHttpServer(99999);
        MainServer.AddHttpServer(httpServer);
        MainServer.Instance = httpServer;
        TestScene? sceneA = null, sceneB = null;
        try
        {
            var userId = TestHelpers.ParseTail(0x1);
            var etmA = new EntityTransferModule();
            var etmB = new EntityTransferModule();
            var lscm = new LocalSimulationConnectorModule();

            IConfigSource config = new IniConfigSource();
            var modules = config.AddConfig("Modules");
            modules.Set("EntityTransferModule", etmA.Name);
            modules.Set("SimulationServices", lscm.Name);

            var sh = new SceneHelpers();
            sceneA = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), 1000, 1000);
            sceneB = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), 1000, 999);

            SceneHelpers.SetupSceneModules(new Scene[] { sceneA, sceneB }, config, lscm);
            SceneHelpers.SetupSceneModules(sceneA, config, new CapabilitiesModule(), etmA);
            SceneHelpers.SetupSceneModules(sceneB, config, new CapabilitiesModule(), etmB);

            var order = new List<string>();
            var crossings = new List<(bool Flying, UUID Region, UUID Avatar)>();
            sceneA.EventManager.OnCrossAgentToNewRegion += (sp, flying, region) =>
            {
                order.Add("cross");
                crossings.Add((flying, region.RegionID, sp.UUID));
            };
            sceneB.EventManager.OnMakeRootAgent += _ => order.Add("root-in-B");

            var acd = SceneHelpers.GenerateAgentData(userId);
            var tc = new TestClient(acd, sceneA);
            var destinationClients = new List<TestClient>();
            EntityTransferHelpers.SetupInformClientOfNeighbourTriggersNeighbourClientCreate(tc, destinationClients);

            var sp = SceneHelpers.AddScenePresence(sceneA, tc, acd);
            sp.AbsolutePosition = new Vector3(128, 32, 10);

            var move = new AgentUpdateArgs
            {
                BodyRotation = Quaternion.CreateFromEulers(new Vector3(0, 0, (float)-(Math.PI / 2))),
                ControlFlags = (uint)(AgentManager.ControlFlags.AGENT_CONTROL_AT_POS | AgentManager.ControlFlags.AGENT_CONTROL_FAST_AT),
            };
            sp.HandleAgentUpdate(sp.ControllingClient, move);

            // Enough updates for the avatar to walk to the northern border.
            for (var i = 0; i < 101; i++) sceneA.Update(1);

            Assert.True(sceneA.GetScenePresence(userId)!.IsChildAgent);
            var inB = sceneB.GetScenePresence(userId)!;
            ((TestClient)inB.ControllingClient).CompleteMovement();
            Assert.False(inB.IsChildAgent);

            var crossing = Assert.Single(crossings);
            Assert.Equal(sceneB.RegionInfo.RegionID, crossing.Region);
            Assert.Equal(userId, crossing.Avatar);
            Assert.Equal("cross,root-in-B", string.Join(",", order));
        }
        finally
        {
            sceneA?.Close();
            sceneB?.Close();
            if (previousServer is not null) MainServer.Instance = previousServer;
            MainServer.RemoveHttpServer(99999);
            Util.FireAndForgetMethod = Util.DefaultFireAndForgetMethod;
        }
    }
}
