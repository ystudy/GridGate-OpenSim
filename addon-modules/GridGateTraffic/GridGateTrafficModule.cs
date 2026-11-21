using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace GridGate.Region.Traffic
{
    /// <summary>
    /// GridGate's parcel traffic counter (spec 2026-11-21-featured-places-design, "Visits"): OpenSim's dwell module, on when
    /// <c>[Dwell] DwellModule = GridGateTraffic</c> (the launcher writes it for every region), in place of
    /// <c>DefaultDwellModule</c>. A root avatar arriving on a parcel counts once a UTC day, except NPCs, the parcel's own owner,
    /// and anyone while the estate blocks dwell. The number fades over a 30-day mean life and lives in the parcel's
    /// <c>Dwell</c>, which the land table stores and About Land shows as Traffic.
    /// </summary>
    /// <remarks>
    /// <para>As an <see cref="IKeepsOwnDwell"/>, it stops <c>LandObject.OnFrame</c> adding avatar-minutes to the same number
    /// (a GridGate change in OpenSim). Before this module, that is what the column held, so on a region's first start with it
    /// (no <see cref="StartedMark"/> in the region's folder) the stored numbers are cleared once, at the first pass a minute in.</para>
    /// <para>OpenSim still changes the number itself in a few places (a join adds the other parcel's, a reclaim clears it);
    /// a value the module didn't write is taken as the parcel's new number.</para>
    /// </remarks>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = ModuleName)]
    public sealed class GridGateTrafficModule : INonSharedRegionModule, IKeepsOwnDwell
    {
        public const string ModuleName = "GridGateTraffic";

        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private static readonly TimeSpan MeanLife = TimeSpan.FromDays(30);
        private static readonly TimeSpan FirstPass = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Pass = TimeSpan.FromHours(1);

        private readonly object _gate = new object();
        private readonly ParcelTraffic _traffic = new ParcelTraffic(MeanLife);
        private readonly Dictionary<Guid, float> _written = new Dictionary<Guid, float>();
        private Scene? _scene;
        private Timer? _timer;
        private string? _folder;
        private bool _firstStart;

        public bool Enabled { get; private set; }

        /// <summary>The file in the region's folder that says the module has run there before.</summary>
        public static string StartedMark(string folder, UUID regionId) =>
            Path.Combine(folder, "GridGateTraffic-" + regionId + ".started");

        /// <summary>The clock; tests set their own.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        public string Name => ModuleName;

        public Type? ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            Enabled = string.Equals(source.Configs["Dwell"]?.GetString("DwellModule", "") ?? "", ModuleName, StringComparison.Ordinal);
            _folder = source.Configs["Startup"]?.GetString("ConfigDirectory", "");
            if (string.IsNullOrEmpty(_folder)) _folder = null;
        }

        public void AddRegion(Scene scene)
        {
            if (!Enabled) return;
            _scene = scene;
            // Without a folder to keep the mark in, nothing is cleared: better old minutes than counts lost at every start.
            _firstStart = _folder is not null && !File.Exists(StartedMark(_folder, scene.RegionInfo.RegionID));
            scene.RegisterModuleInterface<IDwellModule>(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!Enabled) return;
            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnAvatarEnteringNewParcel += OnAvatarEnteringNewParcel;
            _timer = new Timer(_ => SettleAllSafely(), null, FirstPass, Pass);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!Enabled) return;
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnAvatarEnteringNewParcel -= OnAvatarEnteringNewParcel;
            _timer?.Dispose();
            _timer = null;
            SettleAllSafely();
            scene.UnregisterModuleInterface<IDwellModule>(this);
            _scene = null;
        }

        public void Close()
        {
        }

        public int GetDwell(UUID parcelID)
        {
            var parcel = _scene?.LandChannel?.GetLandObject(parcelID);
            return parcel?.LandData is { } land ? GetDwell(land) : 0;
        }

        public int GetDwell(LandData land)
        {
            if (land is null) return 0;
            lock (_gate)
            {
                var now = Time.GetUtcNow();
                Follow(land, now);
                return (int)Math.Round(_traffic.ValueOf(land.GlobalID.Guid, now, SeedOf(land)));
            }
        }

        private void OnNewClient(IClientAPI client) => client.OnParcelDwellRequest += OnParcelDwellRequest;

        private void OnParcelDwellRequest(int localID, IClientAPI client)
        {
            if (_scene?.LandChannel?.GetLandObject(localID)?.LandData is { } land)
                client.SendParcelDwellReply(localID, land.FakeID, GetDwell(land));
        }

        private void OnAvatarEnteringNewParcel(ScenePresence avatar, int localLandID, UUID regionID)
        {
            var scene = _scene;
            if (scene is null || avatar is null || avatar.IsChildAgent || avatar.IsNPC) return;
            if (scene.RegionInfo.EstateSettings.BlockDwell) return;
            var parcel = scene.LandChannel?.GetLandObject(localLandID);
            if (parcel?.LandData is not { } land) return;
            if (!land.IsGroupOwned && land.OwnerID == avatar.UUID) return;

            lock (_gate)
            {
                var now = Time.GetUtcNow();
                Follow(land, now);
                if (!_traffic.Visit(land.GlobalID.Guid, avatar.UUID.Guid, now, SeedOf(land))) return;
                Write(land, _traffic.ValueOf(land.GlobalID.Guid, now, 0));
            }
            Store(scene, parcel);
        }

        /// <summary>
        /// Fades every parcel's number to now and stores each one whose whole number changed (a minute after the region starts,
        /// then hourly, and when it closes), so a place nobody visits drops in the land table too. The first pass on a region's
        /// first start clears OpenSim's old minutes and leaves the mark.
        /// </summary>
        internal void SettleAll()
        {
            var scene = _scene;
            if (scene?.LandChannel is not { } channel) return;
            var firstStart = _firstStart;
            foreach (var parcel in channel.AllParcels())
            {
                if (parcel?.LandData is not { } land) continue;
                bool changed;
                lock (_gate)
                {
                    var now = Time.GetUtcNow();
                    Follow(land, now);
                    var before = land.Dwell;
                    var value = _traffic.Settle(land.GlobalID.Guid, now, SeedOf(land));
                    changed = Math.Round(value) != Math.Round(before);
                    Write(land, value);
                }
                if (changed) Store(scene, parcel);
            }
            if (firstStart && _folder is not null)
            {
                try
                {
                    File.WriteAllText(StartedMark(_folder, scene.RegionInfo.RegionID), "");
                    _firstStart = false;
                }
                catch (Exception e)
                {
                    Log.WarnFormat("[GRIDGATE TRAFFIC]: Could not leave the started mark in {0}: {1}", _folder, e.Message);
                }
            }
        }

        // A thread-pool timer's exception would end the region process.
        private void SettleAllSafely()
        {
            try
            {
                SettleAll();
            }
            catch (Exception e)
            {
                Log.WarnFormat("[GRIDGATE TRAFFIC]: The traffic pass failed: {0}", e.Message);
            }
        }

        /// <summary>The stored number to start a parcel from: none on a region's first start (it holds OpenSim's old minutes).</summary>
        private double SeedOf(LandData land) => _firstStart ? 0 : land.Dwell;

        /// <summary>
        /// Takes a number OpenSim put in the parcel since the module last wrote it (a join, a reclaim) as the parcel's own. OpenSim
        /// also writes back the module's rounded number for parcel info; that is not a change.
        /// </summary>
        private void Follow(LandData land, DateTimeOffset now)
        {
            if (!_written.TryGetValue(land.GlobalID.Guid, out var written)) return;
            if (land.Dwell == written || land.Dwell == (float)Math.Round(written)) return;
            _traffic.Reseed(land.GlobalID.Guid, land.Dwell, now);
            _written[land.GlobalID.Guid] = land.Dwell;
        }

        private void Write(LandData land, double value)
        {
            land.Dwell = (float)value;
            _written[land.GlobalID.Guid] = land.Dwell;
        }

        private static void Store(Scene scene, ILandObject parcel)
        {
            try
            {
                scene.SimulationDataService?.StoreLandObject(parcel);
            }
            catch (Exception e)
            {
                Log.WarnFormat("[GRIDGATE TRAFFIC]: Could not store the traffic of parcel {0}: {1}", parcel.LandData.Name, e.Message);
            }
        }
    }
}
