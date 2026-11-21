using System;
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
    /// <c>DefaultDwellModule</c>, which never counts anything. A root avatar arriving on a parcel counts once a UTC day,
    /// except NPCs, the parcel's own owner, and anyone while the estate blocks dwell. The number fades over a 30-day mean
    /// life and lives in the parcel's <c>Dwell</c>, which the land table stores and About Land shows as Traffic.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = ModuleName)]
    public sealed class GridGateTrafficModule : INonSharedRegionModule, IDwellModule
    {
        public const string ModuleName = "GridGateTraffic";

        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private static readonly TimeSpan MeanLife = TimeSpan.FromDays(30);
        private static readonly TimeSpan Pass = TimeSpan.FromHours(1);

        private readonly object _gate = new object();
        private readonly ParcelTraffic _traffic = new ParcelTraffic(MeanLife);
        private Scene? _scene;
        private Timer? _timer;

        public bool Enabled { get; private set; }

        /// <summary>The clock; tests set their own.</summary>
        internal TimeProvider Time { get; set; } = TimeProvider.System;

        public string Name => ModuleName;

        public Type? ReplaceableInterface => null;

        public void Initialise(IConfigSource source) =>
            Enabled = string.Equals(source.Configs["Dwell"]?.GetString("DwellModule", "") ?? "", ModuleName, StringComparison.Ordinal);

        public void AddRegion(Scene scene)
        {
            if (!Enabled) return;
            _scene = scene;
            scene.RegisterModuleInterface<IDwellModule>(this);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!Enabled) return;
            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnAvatarEnteringNewParcel += OnAvatarEnteringNewParcel;
            _timer = new Timer(_ => SettleAll(), null, Pass, Pass);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!Enabled) return;
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnAvatarEnteringNewParcel -= OnAvatarEnteringNewParcel;
            _timer?.Dispose();
            _timer = null;
            SettleAll();
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
                return (int)Math.Round(_traffic.ValueOf(land.GlobalID.Guid, Time.GetUtcNow(), land.Dwell));
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

            double value;
            lock (_gate)
            {
                if (!_traffic.Visit(land.GlobalID.Guid, avatar.UUID.Guid, Time.GetUtcNow(), land.Dwell)) return;
                value = _traffic.ValueOf(land.GlobalID.Guid, Time.GetUtcNow(), land.Dwell);
            }
            land.Dwell = (float)value;
            Store(scene, parcel);
        }

        /// <summary>
        /// Fades every parcel's number to now and stores each one whose whole number changed (hourly, and when the region
        /// closes), so a place nobody visits drops in the land table too.
        /// </summary>
        internal void SettleAll()
        {
            var scene = _scene;
            if (scene?.LandChannel is not { } channel) return;
            foreach (var parcel in channel.AllParcels())
            {
                if (parcel?.LandData is not { } land) continue;
                double value;
                lock (_gate)
                    value = _traffic.Settle(land.GlobalID.Guid, Time.GetUtcNow(), land.Dwell);
                var changed = Math.Round(value) != Math.Round(land.Dwell);
                land.Dwell = (float)value;
                if (changed) Store(scene, parcel);
            }
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
