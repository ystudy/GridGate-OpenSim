using System.Collections;
using System.Collections.Concurrent;
using System.Xml;
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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Server.Handlers.Simulation;
using OpenSim.Tests.Common;
using Xunit;
using ISimulationService = OpenSim.Services.Interfaces.ISimulationService;

namespace GridGate.Lsl.Tests;

/// <summary>
/// A state from outside this program is stored without PERMISSION_DEBIT: the script engine's state file and the script
/// items of an object marked <see cref="SceneObjectGroup.StateFromOutside"/>. A region start reads them back unmarked
/// (the mark isn't persisted), after a crash before the next save, or in a region whose scripts were off, so nothing
/// stored may keep the grant.
/// </summary>
[Collection(OpenSimScenes.Name)]
public sealed class SavedDebitStateTests
{
    private static readonly UUID Owner = TestHelpers.ParseTail(0x1);
    private static readonly UUID ScriptItem = TestHelpers.ParseTail(0x51);
    private static readonly UUID ScriptAsset = TestHelpers.ParseTail(0x52);
    private const int Anim = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
    private const int Debit = ScriptBaseClass.PERMISSION_DEBIT;

    /// <summary>A script engine that keeps each state it is given, as YEngine writes it to the item's .state file.</summary>
    private sealed class StateStore : IScriptModule
    {
        public ConcurrentDictionary<UUID, string> Stored { get; } = new();

        public bool SetXMLState(UUID itemID, string xml)
        {
            Stored[itemID] = xml;
            return true;
        }

        public string Name => "State store";
        public string ScriptEngineName => Name;
        public Type? ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void AddRegion(Scene scene) => scene.StackModuleInterface<IScriptModule>(this);
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }
        public void Close() { }
#pragma warning disable CS0067
        public event ScriptRemoved? OnScriptRemoved;
        public event ObjectRemoved? OnObjectRemoved;
#pragma warning restore CS0067
        public string GetXMLState(UUID itemID) => string.Empty;
        public bool PostScriptEvent(UUID itemID, string name, object[] args) => false;
        public bool PostObjectEvent(UUID itemID, string name, object[] args) => false;
        public bool SuspendScript(UUID itemID) => false;
        public bool ResumeScript(UUID itemID) => false;
        public ArrayList GetScriptErrors(UUID itemID) => [];
        public bool HasScript(UUID itemID, out bool running) { running = false; return false; }
        public bool GetScriptState(UUID itemID) => false;
        public void SaveAllState() { }
        public void StartProcessing() { }
        public float GetScriptExecutionTime(List<UUID> itemIDs) => 0;
        public int GetScriptsMemory(List<UUID> itemIDs) => 0;
        public Dictionary<uint, float> GetObjectScriptsExecutionTimes() => [];
        public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out float totaltime, out float totalmemory)
        {
            totaltime = 0;
            totalmemory = 0;
            return [];
        }
    }

    /// <summary>One script's state as YEngine saves it (<c>GetXMLState</c>), granted <paramref name="mask"/> by the owner.</summary>
    private static string ScriptState(UUID item, int mask) =>
        $"<State Engine=\"YEngine\" UUID=\"{item}\" Asset=\"{ScriptAsset}\"><ScriptState Engine=\"YEngine\" Asset=\"{ScriptAsset}\">"
        + $"<Permissions granter=\"{Owner}\" mask=\"{mask}\" /></ScriptState></State>";

    /// <summary>An object's script states as a crossing, a teleport or <c>POST /object/</c> carries them.</summary>
    private static string ObjectState(UUID item, int mask) =>
        $"<?xml version=\"1.0\"?><ScriptData><ScriptStates>{ScriptState(item, mask)}</ScriptStates></ScriptData>";

    /// <summary>The granter and mask in a stored state, read where YEngine's <c>LoadScriptState</c> reads them.</summary>
    private static (UUID Granter, int Mask) StoredPermissions(string stored)
    {
        var doc = new XmlDocument();
        doc.LoadXml(stored);
        var permissions = (XmlElement)doc.SelectSingleNode("State/ScriptState/Permissions")!;
        return (new UUID(permissions.GetAttribute("granter")), Convert.ToInt32(permissions.GetAttribute("mask")));
    }

    private static (TestScene Scene, StateStore Store) RegionWithStore()
    {
        var store = new StateStore();
        var scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, store);
        return (scene, store);
    }

    [Fact]
    public void A_marked_groups_stored_state_has_no_debit()
    {
        var (scene, store) = RegionWithStore();
        var group = SceneHelpers.CreateSceneObject(1, Owner);
        group.StateFromOutside = true;

        group.SetState(ObjectState(ScriptItem, Debit | Anim), scene);

        Assert.Equal((Owner, Anim), StoredPermissions(store.Stored[ScriptItem]));
    }

    [Fact]
    public void An_unmarked_groups_stored_state_is_unchanged()
    {
        var (scene, store) = RegionWithStore();
        var group = SceneHelpers.CreateSceneObject(1, Owner);

        group.SetState(ObjectState(ScriptItem, Debit | Anim), scene);

        Assert.Equal(ScriptState(ScriptItem, Debit | Anim), store.Stored[ScriptItem]);
    }

    [Fact]
    public void A_restart_from_a_marked_groups_stored_state_has_no_debit()
    {
        var (scene, store) = RegionWithStore();
        var arrived = SceneHelpers.CreateSceneObject(1, Owner);
        arrived.StateFromOutside = true;
        arrived.SetState(ObjectState(ScriptItem, Debit | Anim), scene);

        // The region crashes before its next save; on the next start the object comes from the region database,
        // unmarked, and YEngine reads the stored state as a region start.
        var reloaded = SceneHelpers.CreateSceneObject(1, Owner);
        var (granter, mask) = StoredPermissions(store.Stored[ScriptItem]);
        var restored = DebitPermission.Restored(mask, granter, reloaded.OwnerID,
            DebitPermission.FromRunningScript(StateSource.RegionStart, reloaded.StateFromOutside, stateIsMarked: true));

        Assert.Equal(Anim, restored);
    }

    [Fact]
    public void A_posted_objects_stored_state_has_no_debit()
    {
        var store = new StateStore();
        var etm = new EntityTransferModule();
        var local = new LocalSimulationConnectorModule();
        IConfigSource config = new IniConfigSource();
        IConfig modules = config.AddConfig("Modules");
        modules.Set("EntityTransferModule", etm.Name);
        modules.Set("SimulationServices", local.Name);
        var scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, config, local, etm, store);
        var sent = SceneHelpers.CreateSceneObject(1, Owner, "Vendor", 0x10);

        new ObjectPost(local).Post(new OSDMap
        {
            ["destination_uuid"] = OSD.FromUUID(scene.RegionInfo.RegionID),
            ["new_position"] = OSD.FromVector3(new Vector3(128, 128, 25)),
            ["sog"] = OSD.FromString(sent.ToXml2()),
            ["extra"] = OSD.FromString(sent.ExtraToXmlString()),
            ["state"] = OSD.FromString(ObjectState(ScriptItem, Debit | Anim)),
        });

        Assert.Equal((Owner, Anim), StoredPermissions(store.Stored[ScriptItem]));
    }

    private sealed class ObjectPost(ISimulationService service) : ObjectSimpleHandler(service)
    {
        public void Post(OSDMap args) => DoObjectPost(args, new TestOSHttpResponse());
    }

    [Fact]
    public void An_archives_saved_state_is_stored_without_debit()
    {
        var store = new StateStore();
        var archiver = new ArchiverModule();
        var scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, archiver, new SerialiserModule(), new TerrainModule(), store);

        var packed = SceneHelpers.CreateSceneObject(1, Owner, "Vendor", 0x10);
        TaskInventoryHelpers.AddScript(scene.AssetService, packed.RootPart, ScriptItem, ScriptAsset, "vendor",
            "default { state_entry() { } }");
        // The object's saved script states, as an OAR saved from a running region carries them.
        string xml = SceneObjectSerializer.ToXml2Format(packed).Replace("</SceneObjectGroup>",
            $"<GroupScriptStates><SavedScriptState UUID=\"{ScriptItem}\">{ScriptState(ScriptItem, Debit | Anim)}"
            + "</SavedScriptState></GroupScriptStates></SceneObjectGroup>");

        var oar = new MemoryStream();
        var tar = new TarArchiveWriter(oar);
        tar.WriteFile(ArchiveConstants.CONTROL_FILE_PATH,
            new ArchiveWriteRequest(scene, (Stream?)null, Guid.Empty).CreateControlFile(new ArchiveScenesGroup()));
        tar.WriteFile(ArchiveConstants.CreateOarObjectPath(packed.Name, packed.UUID, packed.AbsolutePosition), xml);
        tar.Close();

        string? error = "not loaded";
        scene.EventManager.OnOarFileLoaded += (_, _, message) => error = message;
        archiver.DearchiveRegion(new MemoryStream(oar.ToArray()));
        Assert.True(error is null, error);

        // The archiver starts the scripts on a thread of its own, and the state is stored as each one starts.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (store.Stored.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < deadline, "No script state was stored.");
            Thread.Sleep(20);
        }
        Assert.Equal((Owner, Anim), StoredPermissions(Assert.Single(store.Stored).Value));
    }

    [Fact]
    public void A_marked_groups_script_items_keep_no_debit()
    {
        // A posted object's script items carry a granter and a mask of their own, which the region database stores.
        var scene = new SceneHelpers().SetupScene();
        var group = SceneHelpers.CreateSceneObject(1, Owner);
        var item = TaskInventoryHelpers.AddScript(scene.AssetService, group.RootPart);
        item.PermsGranter = Owner;
        item.PermsMask = Debit | Anim;

        group.StateFromOutside = true;

        Assert.Equal(Anim, group.RootPart.Inventory.GetInventoryItem(item.ItemID).PermsMask);
    }

    [Fact]
    public void An_unmarked_groups_script_items_are_unchanged()
    {
        var scene = new SceneHelpers().SetupScene();
        var group = SceneHelpers.CreateSceneObject(1, Owner);
        var item = TaskInventoryHelpers.AddScript(scene.AssetService, group.RootPart);
        item.PermsGranter = Owner;
        item.PermsMask = Debit | Anim;

        group.StateFromOutside = false;

        Assert.Equal(Debit | Anim, group.RootPart.Inventory.GetInventoryItem(item.ItemID).PermsMask);
    }
}
