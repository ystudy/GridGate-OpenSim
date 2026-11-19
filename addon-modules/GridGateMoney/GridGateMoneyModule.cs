using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace GridGate.Region.Money;

/// <summary>
/// GridGate's money module (money design §7.3): turns OpenSim's money events into calls to the launcher's money service.
/// It decides nothing the service decides; its own refusals are in <see cref="MoneyRules"/>, its actions in
/// <see cref="MoneyActions"/>. Chosen by <c>[Economy] EconomyModule = GridGateMoneyModule</c>, which the launcher writes
/// only while money is on.
/// </summary>
[Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GridGateMoneyModule")]
public sealed class GridGateMoneyModule : ISharedRegionModule, IMoneyModule, IVisitorMoneyModule, IClassifiedFeeModule, IFeeHoldModule, IMoneyModuleTellsPayer, IUploadFeeKnown
{
    public const string ModuleName = "GridGateMoneyModule";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
    private static readonly TimeSpan EconomyFresh = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan LandHoldLimit = TimeSpan.FromSeconds(1.5);
    // A parcel's purchase mark this old belongs to a purchase that never finished.
    private static readonly TimeSpan LandMarkStale = TimeSpan.FromMinutes(1);
    private const string LandBusyText = "Someone is buying this parcel right now. Please try again in a moment.";
    private static readonly TimeSpan HealthEvery = TimeSpan.FromMinutes(3);
    // Until the service first answers: no fees are known (so none are offered free, R-8), and visitors and payouts are off.
    private static readonly EconomyInfo NoEconomy = new("C$", 0, 0, false, false, 1_000_000);

    private readonly List<Scene> _scenes = [];
    private readonly ConcurrentDictionary<UUID, MoneyActions> _actions = new();
    private readonly ConcurrentDictionary<UUID, long> _balances = new();
    // A visitor's home grid as their own login gave it, from their last arrival in this program (VisitorHomes, R-3).
    private readonly ConcurrentDictionary<UUID, string> _visitorHomes = new();
    // Land purchases between the hold (validate) and the settle (buy), by region and parcel.
    private readonly ConcurrentDictionary<(UUID Region, int Parcel), LandHold> _landHolds = new();
    private readonly ParcelBuyLimit _buyLimit = new(TimeProvider.System);
    private readonly PayoutLimit _payoutLimit = new(TimeProvider.System);
    private readonly MoneyActions.PayoutPauseState _payoutPause = new();
    private readonly object _economyGate = new();

    private IMoneyService? _client;
    private string _helperUri = "";
    private EconomyInfo? _economy;
    private DateTime _economyAt = DateTime.MinValue;
    private bool _economyRefreshing;
    private ITimer? _healthTimer;

    /// <summary>A parcel's purchase mark; <see cref="At"/> is a monotonic timestamp (<see cref="TimeProvider.GetTimestamp"/>).</summary>
    private sealed record LandHold(UUID Agent, long HoldId, Payer Payer, MoneyWho Seller, long At);

    /// <summary>Whether the INI chose this module and gave it everything it needs.</summary>
    internal bool Enabled { get; private set; }

    /// <summary>False on a Region Server not paired with its grid's money yet: every payment is refused (money plan 3).</summary>
    internal bool IsPaired { get; private set; }

    /// <summary>
    /// The one place reporter of this program (the module is shared by all its regions), made with the first region: one
    /// queue, so a move between two of its regions is handed off before the arrival it vouches for.
    /// </summary>
    internal PlaceReporter? Places { get; private set; }

    public string Name => ModuleName;

    public Type? ReplaceableInterface => null;

    public event ObjectPaid? OnObjectPaid;

    // ---- module life ------------------------------------------------------------------------------

    public void Initialise(IConfigSource source)
    {
        var economy = source.Configs["Economy"];
        if (!string.Equals(economy?.GetString("EconomyModule", "")?.Trim(), ModuleName, StringComparison.Ordinal)) return;

        var cfg = source.Configs["GridGateMoney"];
        string Need(string key) => cfg?.GetString(key, "")?.Trim() ?? "";
        // An unpaired Region Server (money plan 3): the module runs so the region refuses payments and says why.
        if (string.Equals(Need("Paired"), "false", StringComparison.OrdinalIgnoreCase))
        {
            if (Need("HelperURI") is not { Length: > 0 } unpairedHelper)
            {
                Log.Error("[GRIDGATE MONEY]: [GridGateMoney] HelperURI is missing; money stays off in this program.");
                return;
            }
            _client = new UnpairedMoneyService();
            _helperUri = unpairedHelper;
            Enabled = true;
            Log.Info("[GRIDGATE MONEY]: Money is on, but this Region Server isn't paired with its grid's money yet.");
            return;
        }
        var (uri, key, fingerprint, helper) = (Need("ServiceURI"), Need("RegionKey"), Need("ServiceFingerprint"), Need("HelperURI"));
        foreach (var (name, value) in new[] { ("ServiceURI", uri), ("RegionKey", key), ("ServiceFingerprint", fingerprint), ("HelperURI", helper) })
        {
            if (value.Length > 0) continue;
            Log.Error($"[GRIDGATE MONEY]: [GridGateMoney] {name} is missing; money stays off in this program.");
            return;
        }
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var service) || service.Scheme != Uri.UriSchemeHttps)
        {
            Log.Error("[GRIDGATE MONEY]: [GridGateMoney] ServiceURI isn't an https address; money stays off in this program.");
            return;
        }
        _client = new MoneyServiceClient(service, key, fingerprint, why => Log.Warn("[GRIDGATE MONEY]: A money service call failed: " + why));
        _helperUri = helper;
        Enabled = true;
        IsPaired = true;
        Log.Info($"[GRIDGATE MONEY]: Money is on; the money service is at {service.Host}:{service.Port}.");
    }

    public void PostInitialise()
    {
    }

    public void AddRegion(Scene scene)
    {
        if (!Enabled) return;
        // Registered here, not in RegionLoaded: YEngine hooks OnObjectPaid in its own RegionLoaded.
        scene.RegisterModuleInterface<IMoneyModule>(this);
        lock (_scenes)
        {
            _scenes.Add(scene);
            Places ??= new PlaceReporter(_client!, TimeProvider.System, text => Log.Warn("[GRIDGATE MONEY]: " + text));
            _healthTimer ??= TimeProvider.System.CreateTimer(_ => TimedHealth(), null, HealthEvery, HealthEvery);
        }
        var regionId = scene.RegionInfo.RegionID;
        _actions[regionId] = new MoneyActions(_client!, regionId.Guid, KnownEconomy, new View(this)) { Pause = _payoutPause };
        scene.EventManager.OnNewClient += OnNewClient;
        scene.EventManager.OnClientClosed += OnClientClosed;
        // Where avatars are: reported in the background, never holding up a teleport or crossing. A teleport or crossing is
        // remembered when it starts and handed off once the avatar is let go here (or has arrived in another of this
        // program's regions), so one that fails hands nobody over.
        scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
        scene.EventManager.OnTeleportStart += OnTeleportStart;
        scene.EventManager.OnTeleportFail += OnTeleportFail;
        scene.EventManager.OnCrossAgentToNewRegion += OnCrossAgentToNewRegion;
        scene.EventManager.OnMakeChildAgent += OnMakeChildAgent;
        // Reported now and again once the region has started. OpenSim adds the region before registering it with the grid,
        // so a Region Server's key doesn't cover it yet and the service ignores it in this first report.
        scene.EventManager.OnRegionStarted += OnRegionStarted;
        ReportHealth();
    }

    public void RegionLoaded(Scene scene)
    {
        if (!Enabled) return;
        // Here, so they run after the land module's own handlers (which set landValidated and parcelOwnerID).
        scene.EventManager.OnValidateLandBuy += (o, e) => OnValidateLandBuy(scene, e);
        scene.EventManager.OnLandBuy += (o, e) => OnLandBuy(scene, e);
        var features = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
        if (features is not null) features.OnSimulatorFeaturesRequest += OnSimulatorFeaturesRequest;
        RefreshEconomy();
    }

    public void RemoveRegion(Scene scene)
    {
        if (!Enabled) return;
        scene.UnregisterModuleInterface<IMoneyModule>(this);
        scene.EventManager.OnNewClient -= OnNewClient;
        scene.EventManager.OnClientClosed -= OnClientClosed;
        scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
        scene.EventManager.OnTeleportStart -= OnTeleportStart;
        scene.EventManager.OnTeleportFail -= OnTeleportFail;
        scene.EventManager.OnCrossAgentToNewRegion -= OnCrossAgentToNewRegion;
        scene.EventManager.OnMakeChildAgent -= OnMakeChildAgent;
        scene.EventManager.OnRegionStarted -= OnRegionStarted;
        lock (_scenes) _scenes.Remove(scene);
        _actions.TryRemove(scene.RegionInfo.RegionID, out _);
        // At once, so the service stops counting the removed region as run here.
        ReportHealth();
    }

    public void Close()
    {
        _healthTimer?.Dispose();
        Places?.Dispose();
        (_client as IDisposable)?.Dispose();
    }

    // ---- where avatars are --------------------------------------------------------------------------

    /// <summary>The program's regions, at once on each new region and every <see cref="HealthEvery"/>.</summary>
    private void ReportHealth()
    {
        List<Guid> regions;
        lock (_scenes) regions = _scenes.Select(s => s.RegionInfo.RegionID.Guid).ToList();
        Places?.Health(regions);
    }

    /// <summary>The health timer's callback, on a pool thread nobody waits for: a failure (say, at shutdown) is logged by type only.</summary>
    private void TimedHealth()
    {
        try
        {
            ReportHealth();
        }
        catch (Exception e)
        {
            Log.Warn($"[GRIDGATE MONEY]: The health report failed with {e.GetType().Name}.");
        }
    }

    private void OnRegionStarted(Scene scene)
    {
        if (Enabled) ReportHealth();
    }

    private void OnMakeRootAgent(ScenePresence sp) => Places?.Arrived(sp);

    // Raised by the scene the avatar is leaving, so the client's scene is the region it leaves.
    private void OnTeleportStart(IClientAPI client, GridRegion destination, GridRegion finalDestination, uint teleportFlags, bool gridLogout)
    {
        if (client.Scene?.RegionInfo is { } from) Places?.TeleportStarting(client, from.RegionID.Guid, finalDestination, gridLogout);
    }

    private void OnTeleportFail(IClientAPI client, bool gridLogout)
    {
        if (client.Scene?.RegionInfo is { } from) Places?.TeleportFailed(client, from.RegionID.Guid);
    }

    private void OnCrossAgentToNewRegion(ScenePresence sp, bool isFlying, GridRegion newRegion) => Places?.Crossing(sp, newRegion);

    // Raised by the scene the avatar is leaving, once a teleport or crossing out of it has succeeded.
    private void OnMakeChildAgent(ScenePresence sp) => Places?.Released(sp);

    // ---- economy settings ----------------------------------------------------------------------------

    /// <summary>The service's settings, at most a minute old; the last known ones while it can't be reached; null before it ever answered.</summary>
    private EconomyInfo? KnownEconomy()
    {
        lock (_economyGate)
        {
            if (DateTime.UtcNow - _economyAt > EconomyFresh) RefreshEconomy();
            return _economy;
        }
    }

    /// <summary><see cref="KnownEconomy"/>, or <see cref="NoEconomy"/> before the service ever answered.</summary>
    private EconomyInfo Economy() => KnownEconomy() ?? NoEconomy;

    private void RefreshEconomy()
    {
        lock (_economyGate)
        {
            if (_economyRefreshing || _client is null) return;
            _economyRefreshing = true;
        }
        Task.Run(async () =>
        {
            try
            {
                var info = await _client.EconomyAsync(default).ConfigureAwait(false);
                lock (_economyGate)
                {
                    if (info is not null) _economy = info;
                    _economyAt = DateTime.UtcNow;
                }
            }
            finally
            {
                lock (_economyGate) _economyRefreshing = false;
            }
        });
    }

    private void OnSimulatorFeaturesRequest(UUID agentID, ref OSDMap features)
    {
        OSDMap extras;
        if (features.TryGetValue("OpenSimExtras", out var existing) && existing is OSDMap map) extras = map;
        else features["OpenSimExtras"] = extras = new OSDMap();
        extras["currency-base-uri"] = _helperUri;
        extras["currency"] = Economy().CurrencySymbol;
    }

    // ---- viewers ------------------------------------------------------------------------------------

    private void OnNewClient(IClientAPI client)
    {
        if (CircuitHome(client.AgentId) is { } home) _visitorHomes[client.AgentId] = home;
        client.OnMoneyBalanceRequest += (c, agent, session, _) =>
        {
            if (agent != c.AgentId || session != c.SessionId) return;
            Run(() => PushBalanceAsync(c));
        };
        // The packet's source is passed along for MoneyRules to compare; the payer is always this viewer's avatar.
        client.OnMoneyTransferRequest += (source, dest, amount, type, description) => Run(() => TransferAsync(client, source, dest, amount, type));
        client.OnEconomyDataRequest += OnEconomyDataRequest;
        client.OnRequestPayPrice += OnRequestPayPrice;
        client.OnObjectBuy += (c, _, _, _, category, localId, saleType, salePrice) => Run(() => BuyAsync(c, category, localId, saleType, salePrice));
        client.OnCompleteMovementToRegion += (c, _) =>
        {
            if (ActionsFor(c) is { } actions) Run(() => actions.PushEntryBalanceAsync(PayerOf(c)));
        };
    }

    private void OnClientClosed(UUID agent, Scene scene)
    {
        if (FindClient(agent) is not null) return;
        _balances.TryRemove(agent, out _);
        _buyLimit.Forget(agent.Guid);
    }

    private async Task PushBalanceAsync(IClientAPI c)
    {
        var answer = await _client!.BalanceAsync(PayerOf(c).Who, default).ConfigureAwait(false);
        if (answer.Ok && answer.Balance is { } balance) Push(c.AgentId, balance, null);
    }

    private async Task TransferAsync(IClientAPI c, UUID source, UUID dest, int amount, int type)
    {
        if (ActionsFor(c) is not { } actions) return;
        var payer = PayerOf(c);
        switch ((MoneyTransactionType)type)
        {
            case MoneyTransactionType.Gift:
                if (Payee(dest, c.AgentId) is not { } friend) return;
                await actions.PayAvatarAsync(payer, source.Guid, friend, amount).ConfigureAwait(false);
                break;
            case MoneyTransactionType.PayObject:
                if (SceneOf(c)?.GetSceneObjectPart(dest) is not { ParentGroup.RootPart: { } root } part)
                {
                    c.SendAlertMessage("That object isn't here any more.");
                    return;
                }
                if (Payee(root.OwnerID, c.AgentId) is not { } owner) return;
                await actions.PayObjectAsync(payer, source.Guid, Info(part), Info(root), owner, amount).ConfigureAwait(false);
                break;
            default:
                c.SendAlertMessage("That kind of payment isn't available on this grid.");
                break;
        }
    }

    private void OnEconomyDataRequest(IClientAPI c)
    {
        var scene = c.Scene as Scene;
        c.SendEconomyData(1f, scene?.RegionInfo.ObjectCapacity ?? 0, 0, 0, 0, 0, 0f, 10f, 0, 1f, 0, 0, 0, 0, UploadCharge, 0, 2f);
    }

    private void OnRequestPayPrice(IClientAPI c, UUID objectID)
    {
        if (SceneOf(c)?.GetSceneObjectPart(objectID) is { ParentGroup.RootPart: { } root }) c.SendPayPrice(objectID, root.PayPrice);
    }

    private async Task BuyAsync(IClientAPI c, UUID category, uint localId, byte saleType, int salePrice)
    {
        var scene = SceneOf(c);
        if (scene?.GetSceneObjectPart(localId) is not { ParentGroup: { IsDeleted: false } group } part || ActionsFor(c) is not { } actions)
        {
            c.SendAlertMessage("Unable to buy now. The object was not found.");
            return;
        }
        bool Deliver() => scene.RequestModuleInterface<IBuySellModule>()?.BuyObject(c, category, localId, saleType, salePrice) ?? false;
        // The object as it is when this buy's turn comes: another buyer may have bought it, or the owner changed the sale.
        PartInfo? Now() => scene.GetSceneObjectPart(localId) is { ParentGroup: { IsDeleted: false } } p ? Info(p) : null;
        var ownerId = group.RootPart.OwnerID;
        // Nothing is paid for a free sale or one's own object, so a seller who can't be paid doesn't matter there.
        var seller = salePrice > 0 && ownerId != c.AgentId ? Payee(ownerId, c.AgentId) : WhoOf(ownerId);
        if (seller is null) return;
        await actions.BuyAsync(PayerOf(c), Info(part), seller, saleType, salePrice, Deliver, Now).ConfigureAwait(false);
    }

    // ---- land ---------------------------------------------------------------------------------------

    private void OnValidateLandBuy(Scene scene, EventManager.LandBuyArgs e)
    {
        if (!e.landValidated || e.economyValidated) return;
        var land = scene.LandChannel?.GetLandObject(e.parcelLocalID);
        var client = FindClient(e.agentId, scene);
        if (land is null || client is null || ActionsFor(client) is not { } actions) return;
        if (MoneyRules.LandGroupRefused(land.LandData.IsGroupOwned, e.groupOwned))
        {
            client.SendAlertMessage(MoneyRules.GroupLandText);
            return;
        }
        // The price the land module validated is "at least the sale price"; GridGate takes exactly the sale price.
        if (e.parcelPrice != land.LandData.SalePrice || land.LandData.OwnerID != e.parcelOwnerID || land.LandData.Area != e.parcelArea)
        {
            client.SendAlertMessage(MoneyRules.Message(Refusal.PriceChanged));
            return;
        }
        if (e.parcelPrice <= 0)
        {
            e.economyValidated = true;
            return;
        }
        // After a land hold ran out of time, land buys here fail at once for a while (and use nobody's allowance).
        if (actions.LandPaused)
        {
            client.SendAlertMessage(MoneyRules.Message(MoneyAnswer.UnavailableCode));
            return;
        }
        if (Payee(e.parcelOwnerID, e.agentId) is not { } seller) return;
        if (!_buyLimit.TryTake(e.agentId.Guid))
        {
            client.SendAlertMessage(MoneyRules.LandTooFastText);
            return;
        }
        var key = (scene.RegionInfo.RegionID, e.parcelLocalID);
        var payer = PayerOf(client);
        // One purchase of a parcel at a time: the parcel is marked before the service is asked (HoldId 0, nothing held yet),
        // so a second buyer who gets in while the first one waits is turned away. An entry over a minute old (by the
        // monotonic clock, so a wall-clock jump can't age one) is a purchase that never finished, and is taken over.
        var mine = new LandHold(e.agentId, 0, payer, seller, TimeProvider.System.GetTimestamp());
        if (!_landHolds.TryAdd(key, mine))
        {
            if (!_landHolds.TryGetValue(key, out var busy) || TimeProvider.System.GetElapsedTime(busy.At) < LandMarkStale || !_landHolds.TryUpdate(key, mine, busy))
            {
                client.SendAlertMessage(LandBusyText);
                return;
            }
        }
        // This runs on the region's packet thread (ParcelBuy is handled in line), so the service gets a moment only.
        var hold = actions.HoldLandAsync(payer, seller, e.parcelPrice, e.parcelLocalID, LandHoldLimit).GetAwaiter().GetResult();
        if (hold is not { } holdId || holdId == 0)
        {
            _landHolds.TryRemove(KeyValuePair.Create(key, mine));
            return;
        }
        // The mark was taken over while the service was asked: this purchase no longer has the parcel, so OnLandBuy would
        // never settle its hold. It gives the coins back and doesn't go ahead.
        if (!_landHolds.TryUpdate(key, mine with { HoldId = holdId }, mine))
        {
            Run(() => actions.SettleLandAsync(payer, seller, holdId, false));
            client.SendAlertMessage(LandBusyText);
            return;
        }
        // The parcel can change while the service holds the coins (a subdivide makes a new parcel object), so it is read
        // again: what the buyer was shown has to be what is sold, or the hold goes back.
        var after = scene.LandChannel?.GetLandObject(e.parcelLocalID)?.LandData;
        if (after is null || after.OwnerID != e.parcelOwnerID || after.SalePrice != e.parcelPrice || after.Area != e.parcelArea
            || (after.Flags & (uint)(ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects)) == 0
            || (after.AuthBuyerID != UUID.Zero && after.AuthBuyerID != e.agentId))
        {
            _landHolds.TryRemove(KeyValuePair.Create(key, mine with { HoldId = holdId }));
            Run(() => actions.SettleLandAsync(payer, seller, holdId, false));
            client.SendAlertMessage(MoneyRules.Message(Refusal.PriceChanged));
            return;
        }
        e.economyValidated = true;
        e.amountDebited = e.parcelPrice;
    }

    private void OnLandBuy(Scene scene, EventManager.LandBuyArgs e)
    {
        var key = (scene.RegionInfo.RegionID, e.parcelLocalID);
        if (!_landHolds.TryGetValue(key, out var hold) || hold.Agent != e.agentId || hold.HoldId == 0) return;
        // Only this purchase's own mark is removed; its hold is settled either way.
        _landHolds.TryRemove(KeyValuePair.Create(key, hold));
        var data = scene.LandChannel?.GetLandObject(e.parcelLocalID)?.LandData;
        var confirmed = data is not null && (e.groupOwned ? data.GroupID == e.groupId && data.IsGroupOwned : data.OwnerID == e.agentId && !data.IsGroupOwned);
        if (_actions.TryGetValue(scene.RegionInfo.RegionID, out var actions))
            Run(() => actions.SettleLandAsync(hold.Payer, hold.Seller, hold.HoldId, confirmed));
    }

    // ---- IMoneyModule -------------------------------------------------------------------------------

    public int UploadCharge => (int)Math.Clamp(Economy().UploadFee, 0, int.MaxValue);

    /// <summary>False until the money service has answered: <see cref="UploadCharge"/> reads 0 then, which isn't "free".</summary>
    public bool UploadFeeKnown => KnownEconomy() is not null;

    /// <summary>Always 0: GridGate takes no fee for making a group (money design §7.3).</summary>
    public int GroupCreationCharge => 0;

    public int GetBalance(UUID agentID) => _balances.TryGetValue(agentID, out var b) ? (int)Math.Clamp(b, 0, int.MaxValue) : 0;

    public bool UploadCovered(UUID agentID, int amount) =>
        FindClient(agentID) is { } c && ActionsFor(c) is { } actions && actions.UploadCovered(PayerOf(c), amount);

    public bool AmountCovered(UUID agentID, int amount) =>
        FindClient(agentID) is { } c && ActionsFor(c) is { } actions && actions.Covered(PayerOf(c), amount);

    // ---- IClassifiedFeeModule: the grid's fee, never the listing price the viewer typed -----------------------

    public int ClassifiedFee => (int)Math.Clamp(Economy().ClassifiedFee, 0, int.MaxValue);

    public bool ClassifiedFeeCovered(UUID agentID) =>
        FindClient(agentID) is { } c && ActionsFor(c) is { } actions && actions.ClassifiedCovered(PayerOf(c));

    public void ApplyUploadCharge(UUID agentID, int amount, string text) => Charge(agentID, "upload_fee", amount, null);

    public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData = "")
    {
        switch (type)
        {
            case MoneyTransactionType.GroupJoin:
                Charge(agentID, "group_join_fee", amount, Founder(FindClient(agentID), extraData));
                break;
            case MoneyTransactionType.ClassifiedCharge:
                // The grid's fee, whatever listing price the viewer sent.
                if (FindClient(agentID) is { } client && ActionsFor(client) is { } classifieds) classifieds.ChargeClassified(PayerOf(client), amount);
                else Log.Warn($"[GRIDGATE MONEY]: Uncollected classified_fee from {agentID}: not here");
                break;
            default:
                Log.Debug($"[GRIDGATE MONEY]: Ignored a {type} charge of {amount} for {agentID}.");
                break;
        }
    }

    private void Charge(UUID agentID, string type, int amount, Guid? payTo)
    {
        if (amount <= 0) return;
        if (FindClient(agentID) is not { } c || ActionsFor(c) is not { } actions)
        {
            Log.Warn($"[GRIDGATE MONEY]: Uncollected {type} of {amount} from {agentID}: not here");
            return;
        }
        actions.Charge(PayerOf(c), type, amount, payTo);
    }

    /// <summary>
    /// The founder of the group with id <paramref name="groupId"/> (the groups modules pass the id, not a name: proxy groups
    /// of other grids don't have unique names), who a join fee pays; null for none known.
    /// </summary>
    private static Guid? Founder(IClientAPI? client, string groupId) =>
        UUID.TryParse(groupId, out var id) && client?.Scene is Scene s
        && s.RequestModuleInterface<IGroupsModule>()?.GetGroupRecord(id)?.FounderID is { } f && !f.IsZero() ? f.Guid : null;

    // ---- IFeeHoldModule: a fee held before its action -----------------------------------------------------------------

    private static readonly IFeeHold NothingDue = new NoFee();

    /// <summary>
    /// Holds a fee before OpenSim's action: <c>UploadCharge</c> as given, <c>GroupJoin</c> paying the founder of the
    /// group whose id is <paramref name="extraData"/> (as <see cref="ApplyCharge"/> does), <c>ClassifiedCharge</c> at the
    /// grid's fee whatever <paramref name="amount"/> says. A money service from before fee holds can't hold one: the
    /// payer is told money is unavailable. Null when it can't be paid, or the payer isn't here.
    /// </summary>
    public IFeeHold? HoldFee(UUID agentID, int amount, MoneyTransactionType type, string extraData = "")
    {
        var fee = type switch
        {
            MoneyTransactionType.UploadCharge => "upload_fee",
            MoneyTransactionType.GroupJoin => "group_join_fee",
            MoneyTransactionType.ClassifiedCharge => "classified_fee",
            _ => null,
        };
        if (fee is null)
        {
            Log.Debug($"[GRIDGATE MONEY]: Ignored a {type} fee of {amount} for {agentID}.");
            return NothingDue;
        }
        // The classified fee is the grid's own, so it is asked about even when OpenSim passes nothing.
        if (fee != "classified_fee" && amount <= 0) return NothingDue;
        if (FindClient(agentID) is not { } c || ActionsFor(c) is not { } actions)
        {
            // The caller says this in its own words (too few coins), so the log says what really happened.
            Log.Debug($"[GRIDGATE MONEY]: Held no {fee} of {amount} for {agentID}: not here");
            return null;
        }
        var payer = PayerOf(c);
        Guid? payTo = fee == "group_join_fee" && Founder(c, extraData) is { } founder && founder != agentID.Guid ? founder : null;

        if (fee == "classified_fee") amount = actions.ClassifiedFee();
        return actions.HoldFee(payer, fee, amount, payTo) switch
        {
            null => null,
            0 => NothingDue,
            { } holdId => new Held(actions, payer, fee, amount, payTo, holdId),
        };
    }

    private sealed class NoFee : IFeeHold
    {
        public void Settle(bool succeeded)
        {
        }
    }

    /// <summary>A fee the service holds: completed or returned off the caller's thread, once.</summary>
    private sealed class Held(MoneyActions actions, Payer payer, string fee, int amount, Guid? payTo, long holdId) : IFeeHold
    {
        private int _settled;

        public void Settle(bool succeeded)
        {
            if (Interlocked.Exchange(ref _settled, 1) == 0)
                Run(() => actions.SettleFeeAsync(payer, fee, amount, payTo, holdId, succeeded));
        }
    }

    /// <summary>The land module's parcel pass; the only money movement it asks for.</summary>
    public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text)
    {
        if (type != MoneyTransactionType.LandPassSale) return false;
        if (FindClient(fromUser) is not { } c) return false; // nobody here to tell
        if (ActionsFor(c) is not { } actions)
        {
            c.SendAlertMessage(MoneyRules.Message("unavailable"));
            return false;
        }
        // A pass that costs nothing pays nobody.
        var owner = amount > 0 ? Payee(toUser, fromUser) : WhoOf(toUser);
        if (owner is null) return false;
        return actions.MovePass(PayerOf(c), owner, amount, groupLand: GroupLand(c, toUser));
    }

    public void MoveMoney(UUID fromUser, UUID toUser, int amount, string text) =>
        Log.Debug($"[GRIDGATE MONEY]: Ignored an untyped move of {amount} from {fromUser}.");

    public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txn, out string reason)
    {
        Scene? scene = null;
        SceneObjectPart? part = null;
        lock (_scenes)
            foreach (var s in _scenes)
                if (s.GetSceneObjectPart(objectID) is { } p)
                {
                    (scene, part) = (s, p);
                    break;
                }
        if (scene is null || part?.ParentGroup?.RootPart is not { } root || !_actions.TryGetValue(scene.RegionInfo.RegionID, out var actions))
        {
            reason = "SERVICE_ERROR";
            return false;
        }
        if (MoneyRules.CheckPayout(Info(root), fromID.Guid) is { } refused)
        {
            reason = refused;
            return false;
        }
        // A payout the pause refuses anyway doesn't use up the owner's allowance.
        if (actions.PayoutsPaused)
        {
            reason = "SERVICE_ERROR";
            return false;
        }
        if (!_payoutLimit.TryTake(fromID.Guid, out var warn))
        {
            if (warn) Log.Warn($"[GRIDGATE MONEY]: {fromID} has made {PayoutLimit.PerWindow} script payouts in a minute; more are refused until it eases.");
            reason = "SERVICE_ERROR";
            return false;
        }
        var payee = WhoOf(toID, out var unproven);
        if (unproven)
        {
            reason = "LINDENDOLLAR_ENTITYDOESNOTEXIST";
            return false;
        }
        return actions.Payout(fromID.Guid, payee, amount, root.UUID.Guid, root.Name, txn.Guid, out reason);
    }

    /// <summary>Whether <paramref name="owner"/> is a group that owns land in the client's region: a pass's payee.</summary>
    private static bool GroupLand(IClientAPI client, UUID owner) =>
        client.Scene is Scene s && s.LandChannel?.AllParcels() is { } parcels
        && parcels.Any(p => p.LandData is { IsGroupOwned: true } d && (d.OwnerID == owner || d.GroupID == owner));

    // ---- IVisitorMoneyModule ------------------------------------------------------------------------

    public bool CanPayVisitor(UUID agentID)
    {
        if (!Economy().VisitorWallets) return false;
        var um = UserManagement();
        if (um is null || um.IsLocalGridUser(agentID)) return false;
        var who = WhoOf(agentID, out var unproven);
        return !unproven && who.Home.Length > 0;
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private IUserManagement? UserManagement()
    {
        lock (_scenes) return _scenes.Select(s => s.RequestModuleInterface<IUserManagement>()).FirstOrDefault(u => u is not null);
    }

    /// <summary>
    /// An avatar as the ledger keys it: a resident by id, a visitor by id and home grid. A visitor's home comes from their
    /// own login before OpenSim's name cache, which an object's creator data can plant (<see cref="VisitorHomes"/>).
    /// </summary>
    private MoneyWho WhoOf(UUID id) => WhoOf(id, out _);

    /// <summary>
    /// <see cref="WhoOf(UUID)"/>; <paramref name="unproven"/> is true for a visitor who isn't here and whose home is known
    /// only from the name cache: nothing in this program has seen where they come from, so they aren't paid.
    /// </summary>
    private MoneyWho WhoOf(UUID id, out bool unproven)
    {
        unproven = false;
        var um = UserManagement();
        if (um is null || um.IsLocalGridUser(id)) return new MoneyWho(id.Guid, "");
        var circuit = CircuitHome(id);
        if (circuit is not null) _visitorHomes[id] = circuit;
        var remembered = _visitorHomes.TryGetValue(id, out var r) ? r : null;
        unproven = string.IsNullOrWhiteSpace(circuit) && string.IsNullOrWhiteSpace(remembered) && FindClient(id) is null;
        return new MoneyWho(id.Guid, VisitorHomes.Choose(circuit, remembered, um.GetUserHomeURL(id)));
    }

    /// <summary>
    /// Who <paramref name="payer"/>'s payment goes to, or null after telling the payer why not: a visitor who isn't here and
    /// whose home is known only from the name cache isn't paid (see <see cref="WhoOf(UUID, out bool)"/>).
    /// </summary>
    private MoneyWho? Payee(UUID id, UUID payer)
    {
        var who = WhoOf(id, out var unproven);
        if (!unproven) return who;
        FindClient(payer)?.SendAlertMessage(MoneyRules.AbsentVisitorText);
        return null;
    }

    /// <summary>The home grid in the login of an avatar with a circuit in one of this program's regions, or null.</summary>
    private string? CircuitHome(UUID id)
    {
        lock (_scenes)
            foreach (var s in _scenes)
                if (s.AuthenticateHandler?.GetAgentCircuitData(id) is { ServiceURLs: { } urls }
                    && urls.TryGetValue("HomeURI", out var home) && home?.ToString() is { Length: > 0 } h)
                    return h;
        return null;
    }

    private Payer PayerOf(IClientAPI c) => new(c.AgentId.Guid, WhoOf(c.AgentId).Home, c.SessionId.Guid, c.SecureSessionId.Guid);

    private Scene? SceneOf(IClientAPI c) => c.Scene as Scene;

    private MoneyActions? ActionsFor(IClientAPI c) => SceneOf(c) is { } s && _actions.TryGetValue(s.RegionInfo.RegionID, out var a) ? a : null;

    /// <summary>The viewer of an avatar that is in one of this program's regions (not just seeing in), or null.</summary>
    private IClientAPI? FindClient(UUID agent, Scene? prefer = null)
    {
        if (prefer is not null && prefer.TryGetScenePresence(agent, out ScenePresence here) && !here.IsChildAgent) return here.ControllingClient;
        lock (_scenes)
            foreach (var s in _scenes)
                if (s.TryGetScenePresence(agent, out ScenePresence sp) && !sp.IsChildAgent)
                    return sp.ControllingClient;
        return null;
    }

    private static PartInfo Info(SceneObjectPart p)
    {
        var root = p.ParentGroup?.RootPart ?? p;
        var items = root.Inventory.GetInventoryItems();
        var transferable = items.Count > 0 && items.All(i => (i.CurrentPermissions & (uint)OpenSim.Framework.PermissionMask.Transfer) != 0);
        return new PartInfo(p.UUID.Guid, root.UUID.Guid, p.IsRoot, p.OwnerID.Guid, p.GroupID.Guid, p.Name, p.PayPrice, p.ObjectSaleType, p.SalePrice,
            transferable);
    }

    private void Push(UUID agent, long balance, BalanceNote? note)
    {
        _balances[agent] = balance;
        var amount = (int)Math.Clamp(balance, int.MinValue, int.MaxValue);
        lock (_scenes)
            foreach (var s in _scenes)
                if (s.TryGetScenePresence(agent, out ScenePresence sp) && !sp.IsChildAgent)
                    // A fresh transaction id each time: Firestorm drops a repeat (money design §8.8).
                    sp.ControllingClient.SendMoneyBalance(UUID.Random(), true, Array.Empty<byte>(), amount, note?.TransactionType ?? 0,
                        note is null ? UUID.Zero : new UUID(note.Source), false, note is null ? UUID.Zero : new UUID(note.Dest), false,
                        note?.Amount ?? 0, note?.Item ?? "");
    }

    /// <summary>Off the viewer's packet thread; an exception is logged by type, never with what was sent.</summary>
    private static void Run(Func<Task> work) =>
        Util.FireAndForget(_ =>
        {
            try
            {
                work().GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log.Error($"[GRIDGATE MONEY]: A money action failed with {e.GetType().Name}.");
            }
        }, null, "GridGateMoney");

    /// <summary>What <see cref="MoneyActions"/> sees of the region.</summary>
    private sealed class View(GridGateMoneyModule module) : IMoneyView
    {
        public void Tell(Guid agent, string text) => module.FindClient(new UUID(agent))?.SendAlertMessage(text);

        public void PushBalance(Guid agent, long balance, BalanceNote? note) => module.Push(new UUID(agent), balance, note);

        public void ObjectPaid(Guid partId, Guid payer, int amount) => module.OnObjectPaid?.Invoke(new UUID(partId), new UUID(payer), amount);

        public Task Delay(TimeSpan t, CancellationToken ct) => Task.Delay(t, ct);

        public void Warn(string text) => Log.Warn("[GRIDGATE MONEY]: " + text);

        public void Error(string text) => Log.Error("[GRIDGATE MONEY]: " + text);
    }
}
