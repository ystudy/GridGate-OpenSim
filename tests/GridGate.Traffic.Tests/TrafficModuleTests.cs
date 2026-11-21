using GridGate.Region.Traffic;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace GridGate.Traffic.Tests;

/// <summary>OpenSim's test helpers share statics (the console, the scene manager), so these never run alongside each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SceneCollection
{
    public const string Name = "OpenSim test region";
}

/// <summary>The module on a test region: when it turns on, and what counts as a visit (spec 2026-11-21-featured-places-design).</summary>
[Collection(SceneCollection.Name)]
public sealed class TrafficModuleTests
{
    private static readonly UUID Owner = TestHelpers.ParseTail(0x21);
    private static readonly UUID Visitor = TestHelpers.ParseTail(0x22);

    private static IniConfigSource Config(string? dwellModule = GridGateTrafficModule.ModuleName)
    {
        var source = new IniConfigSource();
        if (dwellModule is not null) source.AddConfig("Dwell").Set("DwellModule", dwellModule);
        return source;
    }

    private sealed record Region(Scene Scene, GridGateTrafficModule Module, ILandObject Parcel);

    /// <summary>One parcel over the whole region, owned by <see cref="Owner"/>, with OpenSim's land module and this one.</summary>
    private static Region Build()
    {
        var config = Config();
        var lmm = new LandManagementModule();
        var module = new GridGateTrafficModule();
        var scene = new SceneHelpers().SetupScene("Traffic test region", TestHelpers.ParseTail(0x200), 1000, 1000, config);
        SceneHelpers.SetupSceneModules(scene, config, lmm, module);
        scene.loadAllLandObjectsFromStorage(scene.RegionInfo.RegionID);
        var parcel = lmm.GetLandObject(128, 128);
        parcel.LandData.OwnerID = Owner;
        return new Region(scene, module, parcel);
    }

    private static void Arrive(Region r, UUID who)
    {
        var sp = r.Scene.GetScenePresence(who) ?? SceneHelpers.AddScenePresence(r.Scene, who);
        r.Scene.EventManager.TriggerAvatarEnteringNewParcel(sp, r.Parcel.LandData.LocalID, r.Scene.RegionInfo.RegionID);
    }

    [Fact]
    public void Disabled_without_dwell_setting()
    {
        var module = new GridGateTrafficModule();

        module.Initialise(Config(dwellModule: null));

        Assert.False(module.Enabled);
        module.AddRegion(null!); // disabled: never touches the scene
    }

    [Fact]
    public void Disabled_while_another_dwell_module_is_chosen()
    {
        var module = new GridGateTrafficModule();

        module.Initialise(Config("DefaultDwellModule"));

        Assert.False(module.Enabled);
    }

    [Fact]
    public void Enabled_by_dwell_setting()
    {
        var module = new GridGateTrafficModule();

        module.Initialise(Config());

        Assert.True(module.Enabled);
        Assert.Equal("GridGateTraffic", module.Name);
        Assert.Null(module.ReplaceableInterface);
    }

    [Fact]
    public void Avatar_arriving_counts_on_parcel()
    {
        var r = Build();

        Arrive(r, Visitor);
        Arrive(r, Visitor); // walking out and back in the same day

        Assert.Same(r.Module, r.Scene.RequestModuleInterface<IDwellModule>());
        Assert.Equal(1, r.Module.GetDwell(r.Parcel.LandData));
        Assert.Equal(1, r.Module.GetDwell(r.Parcel.LandData.GlobalID));
        Assert.Equal(1f, r.Parcel.LandData.Dwell, 3);
    }

    [Fact]
    public void Owner_does_not_count()
    {
        var r = Build();

        Arrive(r, Owner);

        Assert.Equal(0, r.Module.GetDwell(r.Parcel.LandData));
    }

    [Fact]
    public void Block_dwell_stops_counting()
    {
        var r = Build();
        r.Scene.RegionInfo.EstateSettings.BlockDwell = true;

        Arrive(r, Visitor);

        Assert.Equal(0, r.Module.GetDwell(r.Parcel.LandData));
    }

    [Fact]
    public void Settling_fades_and_stores_parcels()
    {
        var r = Build();
        Arrive(r, Visitor);
        var start = r.Module.Time.GetUtcNow();
        r.Module.Time = new FixedTime(start + TimeSpan.FromDays(60));

        r.Module.SettleAll();

        Assert.Equal((float)Math.Exp(-2), r.Parcel.LandData.Dwell, 2);
        Assert.Equal(0, r.Module.GetDwell(r.Parcel.LandData));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
