using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

namespace GridGate.Region.Money;

/// <summary>
/// GridGate's money module (money design §7.3): turns OpenSim's money events into calls to the launcher's money service.
/// It decides nothing the service decides; its own refusals are in <see cref="MoneyRules"/>, its actions in
/// <see cref="MoneyActions"/>. Chosen by <c>[Economy] EconomyModule = GridGateMoneyModule</c>, which the launcher writes
/// only while money is on.
/// </summary>
[Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GridGateMoneyModule")]
public sealed class GridGateMoneyModule : ISharedRegionModule, IMoneyModule, IVisitorMoneyModule
{
    public const string ModuleName = "GridGateMoneyModule";
    private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
    private static readonly TimeSpan EconomyFresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LandHoldLimit = TimeSpan.FromSeconds(3);
    private static readonly EconomyInfo NoEconomy = new("C$", 0, 0, true, true, 1_000_000);

    private readonly List<Scene> _scenes = [];
    private readonly ConcurrentDictionary<UUID, MoneyActions> _actions = new();
    private readonly ConcurrentDictionary<UUID, long> _balances = new();
    // Land purchases between the hold (validate) and the settle (buy), by region and parcel.
    private readonly ConcurrentDictionary<(UUID Region, int Parcel), LandHold> _landHolds = new();
    private readonly object _economyGate = new();

    private MoneyServiceClient? _client;
    private string _helperUri = "";
    private EconomyInfo? _economy;
    private DateTime _economyAt = DateTime.MinValue;
    private bool _economyRefreshing;

    private sealed record LandHold(UUID Agent, long HoldId, Payer Payer, MoneyWho Seller, DateTime At);

    /// <summary>Whether the INI chose this module and gave it everything it needs.</summary>
    internal bool Enabled { get; private set; }

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
        _client = new MoneyServiceClient(service, key, fingerprint);
        _helperUri = helper;
        Enabled = true;
    }

    public void PostInitialise()
    {
    }

    public void AddRegion(Scene scene)
    {
        if (!Enabled) return;
        // Registered here, not in RegionLoaded: YEngine hooks OnObjectPaid in its own RegionLoaded.
        scene.RegisterModuleInterface<IMoneyModule>(this);
        lock (_scenes) _scenes.Add(scene);
        var regionId = scene.RegionInfo.RegionID;
        _actions[regionId] = new MoneyActions(_client!, regionId.Guid, Economy, new View(this));
        scene.EventManager.OnNewClient += OnNewClient;
        scene.EventManager.OnClientClosed += OnClientClosed;
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
        lock (_scenes) _scenes.Remove(scene);
        _actions.TryRemove(scene.RegionInfo.RegionID, out _);
    }

    public void Close() => _client?.Dispose();

    // ---- economy settings ----------------------------------------------------------------------------

    /// <summary>The service's settings, at most a minute old; the last known ones (or none) while it can't be reached.</summary>
    private EconomyInfo Economy()
    {
        lock (_economyGate)
        {
            if (DateTime.UtcNow - _economyAt > EconomyFresh) RefreshEconomy();
            return _economy ?? NoEconomy;
        }
    }

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
        if (FindClient(agent) is null) _balances.TryRemove(agent, out _);
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
                await actions.PayAvatarAsync(payer, source.Guid, WhoOf(dest), amount).ConfigureAwait(false);
                break;
            case MoneyTransactionType.PayObject:
                if (SceneOf(c)?.GetSceneObjectPart(dest) is not { ParentGroup.RootPart: { } root } part)
                {
                    c.SendAlertMessage("That object isn't here any more.");
                    return;
                }
                await actions.PayObjectAsync(payer, source.Guid, Info(part), Info(root), WhoOf(root.OwnerID), amount).ConfigureAwait(false);
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
        await actions.BuyAsync(PayerOf(c), Info(part), WhoOf(group.RootPart.OwnerID), saleType, salePrice, Deliver, Now).ConfigureAwait(false);
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
        if (e.parcelPrice != land.LandData.SalePrice || land.LandData.OwnerID != e.parcelOwnerID)
        {
            client.SendAlertMessage(MoneyRules.Message(Refusal.PriceChanged));
            return;
        }
        if (e.parcelPrice <= 0)
        {
            e.economyValidated = true;
            return;
        }
        var key = (scene.RegionInfo.RegionID, e.parcelLocalID);
        // One purchase of a parcel at a time. Validate and buy already run one after another on the region's packet thread,
        // and the land module's own check refuses a parcel no longer for sale; this only guards against that changing.
        if (_landHolds.TryGetValue(key, out var busy) && DateTime.UtcNow - busy.At < TimeSpan.FromMinutes(1))
        {
            client.SendAlertMessage("Someone is buying this parcel right now. Please try again in a moment.");
            return;
        }
        var payer = PayerOf(client);
        var seller = WhoOf(e.parcelOwnerID);
        // This runs on the region's packet thread (ParcelBuy is handled in line), so the service gets a few seconds only.
        var hold = actions.HoldLandAsync(payer, seller, e.parcelPrice, e.parcelLocalID, LandHoldLimit).GetAwaiter().GetResult();
        if (hold is not { } holdId || holdId == 0) return;
        _landHolds[key] = new LandHold(e.agentId, holdId, payer, seller, DateTime.UtcNow);
        e.economyValidated = true;
        e.amountDebited = e.parcelPrice;
    }

    private void OnLandBuy(Scene scene, EventManager.LandBuyArgs e)
    {
        var key = (scene.RegionInfo.RegionID, e.parcelLocalID);
        if (!_landHolds.TryGetValue(key, out var hold) || hold.Agent != e.agentId) return;
        _landHolds.TryRemove(key, out _);
        var data = scene.LandChannel?.GetLandObject(e.parcelLocalID)?.LandData;
        var confirmed = data is not null && (e.groupOwned ? data.GroupID == e.groupId && data.IsGroupOwned : data.OwnerID == e.agentId && !data.IsGroupOwned);
        if (_actions.TryGetValue(scene.RegionInfo.RegionID, out var actions))
            Run(() => actions.SettleLandAsync(hold.Payer, hold.Seller, hold.HoldId, confirmed));
    }

    // ---- IMoneyModule -------------------------------------------------------------------------------

    public int UploadCharge => (int)Math.Clamp(Economy().UploadFee, 0, int.MaxValue);

    /// <summary>Always 0: GridGate takes no fee for making a group (money design §7.3).</summary>
    public int GroupCreationCharge => 0;

    public int GetBalance(UUID agentID) => _balances.TryGetValue(agentID, out var b) ? (int)Math.Clamp(b, 0, int.MaxValue) : 0;

    public bool UploadCovered(UUID agentID, int amount) => AmountCovered(agentID, amount);

    public bool AmountCovered(UUID agentID, int amount) =>
        FindClient(agentID) is { } c && ActionsFor(c) is { } actions && actions.Covered(PayerOf(c), amount);

    public void ApplyUploadCharge(UUID agentID, int amount, string text) => Charge(agentID, "upload_fee", amount, null);

    public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData = "")
    {
        switch (type)
        {
            case MoneyTransactionType.GroupJoin:
                var founder = FindClient(agentID)?.Scene is Scene s ? s.RequestModuleInterface<IGroupsModule>()?.GetGroupRecord(extraData)?.FounderID : null;
                Charge(agentID, "group_join_fee", amount, founder is { } f && !f.IsZero() ? f.Guid : null);
                break;
            case MoneyTransactionType.ClassifiedCharge:
                Charge(agentID, "classified_fee", amount, null);
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

    /// <summary>The land module's parcel pass; the only money movement it asks for.</summary>
    public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text)
    {
        if (type != MoneyTransactionType.LandPassSale) return false;
        return FindClient(fromUser) is { } c && ActionsFor(c) is { } actions && actions.MovePass(PayerOf(c), WhoOf(toUser), amount);
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
        return actions.Payout(fromID.Guid, WhoOf(toID), amount, root.UUID.Guid, root.Name, txn.Guid, out reason);
    }

    // ---- IVisitorMoneyModule ------------------------------------------------------------------------

    public bool CanPayVisitor(UUID agentID)
    {
        if (!Economy().VisitorWallets) return false;
        var um = UserManagement();
        return um is not null && !um.IsLocalGridUser(agentID) && !string.IsNullOrEmpty(um.GetUserHomeURL(agentID));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private IUserManagement? UserManagement()
    {
        lock (_scenes) return _scenes.Select(s => s.RequestModuleInterface<IUserManagement>()).FirstOrDefault(u => u is not null);
    }

    /// <summary>An avatar as the ledger keys it: a resident by id, a visitor by id and home grid.</summary>
    private MoneyWho WhoOf(UUID id)
    {
        var um = UserManagement();
        if (um is null || um.IsLocalGridUser(id)) return new MoneyWho(id.Guid, "");
        return new MoneyWho(id.Guid, um.GetUserHomeURL(id) ?? "");
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

        public Task Delay(TimeSpan t) => Task.Delay(t);

        public void Warn(string text) => Log.Warn("[GRIDGATE MONEY]: " + text);

        public void Error(string text) => Log.Error("[GRIDGATE MONEY]: " + text);
    }
}
