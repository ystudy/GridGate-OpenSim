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
    private static readonly UUID Second = TestHelpers.ParseTail(0x23);
    private static readonly UUID RegionId = TestHelpers.ParseTail(0x200);

    private static IniConfigSource Config(string? dwellModule = GridGateTrafficModule.ModuleName, string? folder = null)
    {
        var source = new IniConfigSource();
        if (dwellModule is not null) source.AddConfig("Dwell").Set("DwellModule", dwellModule);
        if (folder is not null) source.AddConfig("Startup").Set("ConfigDirectory", folder);
        return source;
    }

    private sealed record Region(Scene Scene, GridGateTrafficModule Module, ILandObject Parcel, string Folder);

    /// <summary>
    /// One parcel over the whole region, owned by <see cref="Owner"/>, with OpenSim's land module and this one. The region's
    /// folder already has the module's "started" mark unless <paramref name="firstStart"/>.
    /// </summary>
    private static Region Build(bool firstStart = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "gg-traffic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (!firstStart) File.WriteAllText(GridGateTrafficModule.StartedMark(folder, RegionId), "");
        var config = Config(folder: folder);
        var lmm = new LandManagementModule();
        var module = new GridGateTrafficModule();
        var scene = new SceneHelpers().SetupScene("Traffic test region", RegionId, 1000, 1000, config);
        SceneHelpers.SetupSceneModules(scene, config, lmm, module);
        scene.loadAllLandObjectsFromStorage(scene.RegionInfo.RegionID);
        var parcel = lmm.GetLandObject(128, 128);
        parcel.LandData.OwnerID = Owner;
        return new Region(scene, module, parcel, folder);
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

    /// <summary>
    /// OpenSim's own dwell sum (LandObject.OnFrame: avatar-minutes, fading over about 18 hours, every 2.5 minutes) must leave
    /// GridGate's visit count alone, or the directory would show minutes spent instead of visits.
    /// </summary>
    [Fact]
    public void OpenSims_own_minutes_do_not_replace_the_visit_count()
    {
        var r = Build();
        Arrive(r, Visitor);
        var sp = r.Scene.GetScenePresence(Visitor);
        var now = Util.GetTimeStampMS();
        sp.currentParcelUUID = r.Parcel.LandData.GlobalID;
        sp.ParcelDwellTickMS = now - 10 * 60_000; // standing there ten minutes
        r.Parcel.LandData.LastDwellTimeMS = now - 200_000; // past OpenSim's 2.5-minute step

        r.Scene.EventManager.TriggerOnFrame();

        Assert.Equal(1f, r.Parcel.LandData.Dwell, 2);
    }

    /// <summary>On a region's first start with the module, the column holds OpenSim's old minutes: they are cleared once.</summary>
    [Fact]
    public void First_start_clears_old_minutes_once()
    {
        var r = Build(firstStart: true);
        r.Parcel.LandData.Dwell = 500; // OpenSim's avatar-minutes, as stored before

        r.Module.SettleAll();

        Assert.Equal(0f, r.Parcel.LandData.Dwell, 3);
        Assert.True(File.Exists(GridGateTrafficModule.StartedMark(r.Folder, RegionId)));
    }

    [Fact]
    public void Later_starts_keep_the_stored_count()
    {
        var r = Build();
        r.Parcel.LandData.Dwell = 40; // GridGate's own count from before a restart

        r.Module.SettleAll();

        Assert.Equal(40f, r.Parcel.LandData.Dwell, 1);
        Arrive(r, Visitor);
        Assert.Equal(41, r.Module.GetDwell(r.Parcel.LandData));
    }

    /// <summary>OpenSim changes the number itself in a few places (a join adds the other parcel's, a reclaim clears it): kept.</summary>
    [Fact]
    public void A_change_made_by_OpenSim_is_kept()
    {
        var r = Build();
        Arrive(r, Visitor);

        r.Parcel.LandData.Dwell = 6; // a join added the other parcel's traffic

        Assert.Equal(6, r.Module.GetDwell(r.Parcel.LandData));
        Arrive(r, Second);
        Assert.Equal(7f, r.Parcel.LandData.Dwell, 2);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
