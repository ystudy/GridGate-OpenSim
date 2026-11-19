using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Serialization;
using OpenSim.Region.CoreModules.Framework.EntityTransfer;
using OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation;
using OpenSim.Region.CoreModules.World.Archiver;
using OpenSim.Region.CoreModules.World.Serialiser;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Server.Handlers.Simulation;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using ISimulationService = OpenSim.Services.Interfaces.ISimulationService;
using OpenSim.Tests.Common;
using Xunit;

namespace GridGate.Lsl.Tests;

/// <summary>
/// Where a scene object's saved script state can come from outside this program, and that the object is marked
/// <see cref="SceneObjectGroup.StateFromOutside"/> there, so YEngine does not restore a PERMISSION_DEBIT from it. A
/// crossing or a teleport between two regions of this program copies the object locally and is not marked.
/// </summary>
/// <remarks>
/// In <see cref="OpenSimScenes"/>: every new <see cref="SceneHelpers"/> makes its scene manager the static one an OAR load
/// looks its region up in, so these must not run beside other tests that set up scenes.
/// </remarks>
[Collection(OpenSimScenes.Name)]
public sealed class DebitStateIngressTests
{
    private static readonly UUID Owner = TestHelpers.ParseTail(0x1);
    private const int Anim = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
    private const int Debit = ScriptBaseClass.PERMISSION_DEBIT;

    private sealed record Regions(TestScene A, TestScene B, LocalSimulationConnectorModule Local);

    /// <summary>Two regions of one program, joined by OpenSim's local simulation connector.</summary>
    private static Regions TwoRegions()
    {
        var etmA = new EntityTransferModule();
        var etmB = new EntityTransferModule();
        var local = new LocalSimulationConnectorModule();

        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etmA.Name);
        modules.Set("SimulationServices", local.Name);

        var sh = new SceneHelpers();
        var a = sh.SetupScene("sceneA", TestHelpers.ParseTail(0x100), 1000, 1000);
        var b = sh.SetupScene("sceneB", TestHelpers.ParseTail(0x200), 1000, 999);
        SceneHelpers.SetupSceneModules(new Scene[] { a, b }, config, local);
        SceneHelpers.SetupSceneModules(a, config, etmA);
        SceneHelpers.SetupSceneModules(b, config, etmB);
        UserAccountHelpers.CreateUserWithInventory(a, Owner);
        return new Regions(a, b, local);
    }

    private static GridRegion Destination(Scene s) => new()
    {
        RegionID = s.RegionInfo.RegionID,
        RegionLocX = (int)s.RegionInfo.WorldLocX,
        RegionLocY = (int)s.RegionInfo.WorldLocY,
        RegionName = s.RegionInfo.RegionName,
    };

    /// <summary>OpenSim's <c>POST /object/</c> handler, called as the HTTP server calls it.</summary>
    private sealed class ObjectPost(ISimulationService service) : ObjectSimpleHandler(service)
    {
        public void Post(OSDMap args) => DoObjectPost(args, new TestOSHttpResponse());
    }

    [Fact]
    public void An_object_posted_by_another_program_is_marked()
    {
        var r = TwoRegions();
        var sent = SceneHelpers.CreateSceneObject(1, Owner, "Vendor", 0x10);

        new ObjectPost(r.Local).Post(new OSDMap
        {
            ["destination_uuid"] = OSD.FromUUID(r.B.RegionInfo.RegionID),
            ["destination_x"] = OSD.FromInteger((int)r.B.RegionInfo.WorldLocX),
            ["destination_y"] = OSD.FromInteger((int)r.B.RegionInfo.WorldLocY),
            ["destination_name"] = OSD.FromString(r.B.RegionInfo.RegionName),
            ["new_position"] = OSD.FromVector3(new Vector3(128, 128, 25)),
            ["sog"] = OSD.FromString(sent.ToXml2()),
            ["extra"] = OSD.FromString(sent.ExtraToXmlString()),
        });

        var arrived = r.B.GetSceneObjectGroup(sent.UUID);
        Assert.NotNull(arrived);
        Assert.True(arrived.StateFromOutside);
    }

    [Fact]
    public void A_local_crossing_is_not_marked()
    {
        var r = TwoRegions();
        var group = SceneHelpers.AddSceneObject(r.A, 1, Owner, "Vendor", 0x10);

        // What EntityTransferModule does when an object crosses into a region of this program.
        Assert.True(r.Local.CreateObject(Destination(r.B), new Vector3(128, 128, 25), group, isLocalCall: true));

        var arrived = r.B.GetSceneObjectGroup(group.UUID);
        Assert.NotNull(arrived);
        Assert.NotSame(group, arrived);
        Assert.False(arrived.StateFromOutside);
    }

    [Fact]
    public void A_copy_for_a_new_region_does_not_carry_the_mark()
    {
        // A local crossing and a local teleport's attachments both go through CloneForNewScene, with a state snapshot
        // taken from this program's running scripts, which have already left out a debit from outside.
        var scene = new SceneHelpers().SetupScene();
        var group = SceneHelpers.AddSceneObject(scene, 1, Owner, "Vendor", 0x10);
        group.StateFromOutside = true;

        var copy = (SceneObjectGroup)group.CloneForNewScene();

        Assert.True(group.StateFromOutside);
        Assert.False(copy.StateFromOutside);
    }

    [Fact]
    public void An_arriving_agents_attachments_from_another_program_are_marked()
    {
        var r = TwoRegions();
        var attachment = SceneHelpers.CreateSceneObject(1, Owner, "Hud", 0x10);
        var sent = new AgentData
        {
            AgentID = Owner,
            RegionID = r.A.RegionInfo.RegionID,
            AttachmentObjects = [attachment],
            AttachmentObjectStates = [""],
        };
        var ctx = new EntityTransferContext();

        // How AgentHandlers reads an agent update that came over HTTP from another program.
        var received = new AgentData();
        received.Unpack(sent.Pack(ctx), r.B, ctx);

        var arrived = Assert.IsType<SceneObjectGroup>(Assert.Single(received.AttachmentObjects));
        Assert.True(arrived.StateFromOutside);
    }

    [Fact]
    public void A_debit_in_an_archive_is_dropped()
    {
        var archiver = new ArchiverModule();
        var scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, archiver, new SerialiserModule(), new TerrainModule());

        var oar = new MemoryStream();
        var tar = new TarArchiveWriter(oar);
        tar.WriteFile(ArchiveConstants.CONTROL_FILE_PATH,
            new ArchiveWriteRequest(scene, (Stream?)null, Guid.Empty).CreateControlFile(new ArchiveScenesGroup()));
        var packed = SceneHelpers.CreateSceneObject(1, Owner, "Vendor", 0x10);
        tar.WriteFile(ArchiveConstants.CreateOarObjectPath(packed.Name, packed.UUID, packed.AbsolutePosition),
            SceneObjectSerializer.ToXml2Format(packed));
        tar.Close();

        string? error = "not loaded";
        scene.EventManager.OnOarFileLoaded += (_, _, message) => error = message;
        archiver.DearchiveRegion(new MemoryStream(oar.ToArray()));
        Assert.True(error is null, error);

        // An OAR load gives its objects new ids and starts their scripts as at a region start.
        var loaded = Assert.Single(scene.GetSceneObjectGroups(), g => g.Name == packed.Name);
        Assert.True(loaded.StateFromOutside);
        Assert.Equal(Anim, DebitPermission.Restored(Debit | Anim, Owner, loaded.OwnerID,
            DebitPermission.FromRunningScript(StateSource.RegionStart, loaded.StateFromOutside, stateIsMarked: true)));
    }

    [Fact]
    public void An_object_loaded_from_an_xml2_file_is_marked()
    {
        var scene = new SceneHelpers().SetupScene();
        var packed = SceneHelpers.CreateSceneObject(1, Owner, "Vendor", 0x10);

        SceneXmlLoader.LoadPrimsFromXml2(scene, new StringReader($"<scene>{packed.ToXml2()}</scene>"), startScripts: false);

        var loaded = scene.GetSceneObjectGroup(packed.UUID);
        Assert.NotNull(loaded);
        Assert.True(loaded.StateFromOutside);
    }

    [Fact]
    public void An_object_loaded_from_an_xml_file_is_marked()
    {
        var scene = new SceneHelpers().SetupScene();
        var packed = SceneHelpers.CreateSceneObject(1, Owner, "Vendor", 0x10);
        var file = Path.Combine(Path.GetTempPath(), $"gridgate-load-xml-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, $"<scene>{SceneObjectSerializer.ToOriginalXmlFormat(packed)}</scene>");
        try
        {
            SceneXmlLoader.LoadPrimsFromXml(scene, file, newIDS: false, Vector3.Zero);
        }
        finally
        {
            File.Delete(file);
        }

        var loaded = scene.GetSceneObjectGroup(packed.UUID);
        Assert.NotNull(loaded);
        Assert.True(loaded.StateFromOutside);
    }
}
