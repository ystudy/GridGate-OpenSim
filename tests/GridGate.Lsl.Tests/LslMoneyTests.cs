using System.Collections.Concurrent;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Tests.Common;
using Xunit;

namespace GridGate.Lsl.Tests;

/// <summary>
/// GridGate's changes to OpenSim's script money functions (MERGING.md, money design §12), tried against a real LSL_Api
/// in a test region with a money module that records what it is asked to pay.
/// </summary>
public sealed class LslMoneyTests
{
    private static readonly UUID Owner = TestHelpers.ParseTail(0x1);
    private static readonly UUID Stranger = TestHelpers.ParseTail(0x2);
    private static readonly UUID Payee = TestHelpers.ParseTail(0x3);
    private static readonly UUID OtherPayee = TestHelpers.ParseTail(0x4);
    private static readonly UUID Group = TestHelpers.ParseTail(0x5);
    private static readonly UUID Visitor = TestHelpers.ParseTail(0x6);

    /// <summary>How long a refused payment is given to show up anyway, after a later allowed one has arrived.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private sealed record Region(Scene Scene, MockScriptEngine Engine, RecordingMoney Money, SceneObjectPart Part, TaskInventoryItem Item, LSL_Api Api);

    private static Region Setup(bool autoGrantAll = false, bool visitorsPayable = false)
    {
        var engine = new MockScriptEngine();
        var money = new RecordingMoney(visitorsPayable);
        var scene = new SceneHelpers().SetupScene();
        SceneHelpers.SetupSceneModules(scene, engine, money);
        UserAccountHelpers.CreateUserWithInventory(scene, Owner);
        UserAccountHelpers.CreateUserWithInventory(scene, Payee);
        UserAccountHelpers.CreateUserWithInventory(scene, OtherPayee);
        if (autoGrantAll)
            scene.StoreExtraSetting("auto_grant_all_perms", "true");

        var part = SceneHelpers.AddSceneObject(scene, "Payer", Owner).RootPart;
        var item = TaskInventoryHelpers.AddScript(scene.AssetService, part);
        var api = new LSL_Api();
        api.Initialize(engine, part, item);
        return new Region(scene, engine, money, part, item, api);
    }

    private static void Grant(Region r, UUID granter, int mask = ScriptBaseClass.PERMISSION_DEBIT)
    {
        r.Item.PermsGranter = granter;
        r.Item.PermsMask = mask;
    }

    /// <summary>Waits until a payment to <paramref name="to"/> arrives.</summary>
    private static async Task<RecordingMoney.Payment> Paid(Region r, UUID to)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var p = r.Money.Payments.FirstOrDefault(x => x.To == to);
            if (p is not null) return p;
            Assert.True(DateTime.UtcNow < deadline, $"No payment to {to} arrived.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Asserts that nothing was paid to <paramref name="to"/>. The payment runs on OpenSim's dataserver work pool, so an
    /// allowed payment to <see cref="OtherPayee"/> is made after the refused one and waited for: the refused one, had it
    /// been queued, would have been queued first.
    /// </summary>
    private static async Task NothingPaid(Region r, UUID to)
    {
        Grant(r, Owner);
        r.Api.llGiveMoney(OtherPayee.ToString(), 1);
        await Paid(r, OtherPayee);
        await Task.Delay(Settle, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(r.Money.Payments, p => p.To == to);
    }

    private static List<EventParams> Events(Region r, string name) =>
        r.Engine.PostedEvents.TryGetValue(r.Item.ItemID, out var list) ? list.Where(e => e.EventName == name).ToList() : [];

    private static async Task<EventParams> TransactionResult(Region r)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            lock (r.Engine.PostedEvents)
            {
                var e = Events(r, "transaction_result").FirstOrDefault();
                if (e is not null) return e;
            }
            Assert.True(DateTime.UtcNow < deadline, "No transaction_result event arrived.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    // --- The control: an owner-granted payment goes through. -------------------------------------------------------

    [Fact]
    public async Task Owner_granted_llGiveMoney_pays()
    {
        var r = Setup();
        Grant(r, Owner);

        r.Api.llGiveMoney(Payee.ToString(), 5);

        var p = await Paid(r, Payee);
        Assert.Equal(5, p.Amount);
        Assert.Equal(Owner, p.From);
        Assert.Equal(r.Part.ParentGroup.RootPart.UUID, p.ObjectId);
    }

    // --- PERMISSION_DEBIT must come from the object's owner. -------------------------------------------------------

    [Fact]
    public async Task llGiveMoney_refuses_debit_granted_by_someone_else()
    {
        var r = Setup();
        Grant(r, Stranger);

        r.Api.llGiveMoney(Payee.ToString(), 5);

        await NothingPaid(r, Payee);
    }

    [Fact]
    public async Task llTransferLindenDollars_refuses_debit_granted_by_someone_else()
    {
        var r = Setup();
        Grant(r, Stranger);

        r.Api.llTransferLindenDollars(Payee.ToString(), 5);

        var result = await TransactionResult(r);
        Assert.Equal(0, ((LSL_Types.LSLInteger)result.Params[1]).value);
        Assert.Equal("MISSING_PERMISSION_DEBIT", result.Params[2].ToString());
        await NothingPaid(r, Payee);
    }

    [Fact]
    public async Task Owner_granted_llTransferLindenDollars_pays()
    {
        var r = Setup();
        Grant(r, Owner);

        r.Api.llTransferLindenDollars(Payee.ToString(), 5);

        var result = await TransactionResult(r);
        Assert.Equal(1, ((LSL_Types.LSLInteger)result.Params[1]).value);
        Assert.Equal(5, (await Paid(r, Payee)).Amount);
    }

    // --- llGiveMoney refuses amounts of 0 or less. ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task llGiveMoney_refuses_amounts_of_zero_or_less(int amount)
    {
        var r = Setup();
        Grant(r, Owner);

        r.Api.llGiveMoney(Payee.ToString(), amount);

        await NothingPaid(r, Payee);
    }

    // --- llGiveMoney refuses group-owned objects. -------------------------------------------------------------------

    [Fact]
    public async Task llGiveMoney_refuses_group_owned_objects()
    {
        var r = Setup();
        r.Part.GroupID = Group;
        r.Part.OwnerID = Group;
        // Granted by the owner (the group), so only the group-owned rule stands in the way.
        Grant(r, Group);

        r.Api.llGiveMoney(Payee.ToString(), 5);

        // NothingPaid's control payment needs a person-owned object again.
        r.Part.OwnerID = Owner;
        await NothingPaid(r, Payee);
    }

    // --- auto_grant_all_perms never grants PERMISSION_DEBIT. --------------------------------------------------------

    [Theory]
    [InlineData(ScriptBaseClass.PERMISSION_DEBIT)]
    [InlineData(ScriptBaseClass.PERMISSION_DEBIT | ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION)]
    public void auto_grant_all_perms_does_not_grant_debit(int requested)
    {
        var r = Setup(autoGrantAll: true);
        Assert.Equal("true", r.Scene.GetExtraSetting("auto_grant_all_perms"));

        // Even the owner has to say yes in the viewer; the owner isn't in the region here, so nobody can.
        r.Api.llRequestPermissions(Owner.ToString(), requested);

        Assert.Equal(0, r.Item.PermsMask & ScriptBaseClass.PERMISSION_DEBIT);
        var answer = Assert.Single(Events(r, "run_time_permissions"));
        Assert.Equal(0, ((LSL_Types.LSLInteger)answer.Params[0]).value);
    }

    [Fact]
    public void auto_grant_all_perms_still_grants_everything_else()
    {
        var r = Setup(autoGrantAll: true);
        var requested = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION | ScriptBaseClass.PERMISSION_TAKE_CONTROLS;

        r.Api.llRequestPermissions(Owner.ToString(), requested);

        Assert.Equal(requested, r.Item.PermsMask);
        var answer = Assert.Single(Events(r, "run_time_permissions"));
        Assert.Equal(requested, ((LSL_Types.LSLInteger)answer.Params[0]).value);
    }

    // --- llTransferLindenDollars can pay hypergrid visitors. --------------------------------------------------------

    [Fact]
    public async Task llTransferLindenDollars_pays_a_visitor_the_money_module_can_pay()
    {
        var r = Setup(visitorsPayable: true);
        Grant(r, Owner);

        r.Api.llTransferLindenDollars(Visitor.ToString(), 5);

        var result = await TransactionResult(r);
        Assert.Equal(1, ((LSL_Types.LSLInteger)result.Params[1]).value);
        Assert.Equal(5, (await Paid(r, Visitor)).Amount);
    }

    [Fact]
    public async Task llTransferLindenDollars_refuses_a_visitor_the_money_module_cannot_pay()
    {
        var r = Setup(visitorsPayable: false);
        Grant(r, Owner);

        r.Api.llTransferLindenDollars(Visitor.ToString(), 5);

        var result = await TransactionResult(r);
        Assert.Equal(0, ((LSL_Types.LSLInteger)result.Params[1]).value);
        Assert.Equal("LINDENDOLLAR_ENTITYDOESNOTEXIST", result.Params[2].ToString());
        await NothingPaid(r, Visitor);
    }

    /// <summary>A money module that pays whatever it is asked and remembers it; <see cref="Visitor"/> is a visitor.</summary>
    private sealed class RecordingMoney(bool visitorsPayable) : INonSharedRegionModule, IMoneyModule, IVisitorMoneyModule
    {
        public sealed record Payment(UUID ObjectId, UUID From, UUID To, int Amount);

        private readonly ConcurrentQueue<Payment> _payments = new();
        public IReadOnlyCollection<Payment> Payments => _payments.ToArray();

        public string Name => "RecordingMoney";
        public Type? ReplaceableInterface => null;
        public void Initialise(IConfigSource source) { }
        public void Close() { }
        public void AddRegion(Scene scene) => scene.RegisterModuleInterface<IMoneyModule>(this);
        public void RemoveRegion(Scene scene) { }
        public void RegionLoaded(Scene scene) { }

        public bool ObjectGiveMoney(UUID objectID, UUID fromID, UUID toID, int amount, UUID txn, out string reason)
        {
            _payments.Enqueue(new Payment(objectID, fromID, toID, amount));
            reason = string.Empty;
            return true;
        }

        public bool CanPayVisitor(UUID agentID) => visitorsPayable && agentID == Visitor;

        public int GetBalance(UUID agentID) => 0;
        public bool UploadCovered(UUID agentID, int amount) => true;
        public bool AmountCovered(UUID agentID, int amount) => true;
        public void ApplyCharge(UUID agentID, int amount, MoneyTransactionType type, string extraData = "") { }
        public void ApplyUploadCharge(UUID agentID, int amount, string text) { }
        public void MoveMoney(UUID fromUser, UUID toUser, int amount, string text) { }
        public bool MoveMoney(UUID fromUser, UUID toUser, int amount, MoneyTransactionType type, string text) => true;
        public int UploadCharge => 0;
        public int GroupCreationCharge => 0;
#pragma warning disable CS0067 // never raised: no one pays objects here
        public event ObjectPaid? OnObjectPaid;
#pragma warning restore CS0067
    }
}
