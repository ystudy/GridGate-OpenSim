using System.Collections.Concurrent;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.Avatar.Attachments;
using OpenSim.Region.CoreModules.Framework.InventoryAccess;
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

    private static Region Setup(bool autoGrantAll = false, bool visitorsPayable = false, bool attachments = false)
    {
        var engine = new MockScriptEngine();
        var money = new RecordingMoney(visitorsPayable);
        var scene = new SceneHelpers().SetupScene();
        if (attachments)
        {
            // OpenSim's own attachments module, as its AttachmentsModuleTests set it up.
            IConfigSource config = new IniConfigSource();
            config.AddConfig("Modules").Set("InventoryAccessModule", "BasicInventoryAccessModule");
            SceneHelpers.SetupSceneModules(scene, config, engine, money, new AttachmentsModule(), new BasicInventoryAccessModule());
        }
        else
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

    /// <summary>money review 2026-11-15, R-10: llTransferLindenDollars refuses group-owned objects, and says why.</summary>
    [Fact]
    public async Task llTransferLindenDollars_refuses_group_owned_objects()
    {
        var r = Setup();
        r.Part.GroupID = Group;
        r.Part.OwnerID = Group;
        Grant(r, Group);

        r.Api.llTransferLindenDollars(Payee.ToString(), 5);

        var result = await TransactionResult(r);
        Assert.Equal(0, ((LSL_Types.LSLInteger)result.Params[1]).value);
        Assert.Equal("GROUP_OWNED", result.Params[2].ToString());
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

    // --- Only the owner is ever asked for PERMISSION_DEBIT, and an answer grants only what its own script asked
    //     (money review 2026-11-15, R-1 and R-7). ----------------------------------------------------------------------

    private static ScenePresence Present(Region r, UUID id)
    {
        if (r.Scene.UserAccountService.GetUserAccount(r.Scene.RegionInfo.ScopeID, id) is null)
            UserAccountHelpers.CreateUserWithInventory(r.Scene, id);
        return SceneHelpers.AddScenePresence(r.Scene, id);
    }

    /// <summary>What the viewer's ScriptAnswerYes packet does: OpenSim hands it to the script's answer handler.</summary>
    private static void Answer(Region r, ScenePresence who, UUID itemId, int answer) =>
        typeof(LSL_Api).GetMethod("handleScriptAnswer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(r.Api, [who.ControllingClient, r.Part.UUID, itemId, answer]);

    private const int Anim = ScriptBaseClass.PERMISSION_TRIGGER_ANIMATION;
    private const int Debit = ScriptBaseClass.PERMISSION_DEBIT;

    [Fact]
    public async Task The_owners_yes_grants_debit_and_pays()
    {
        var r = Setup();
        var owner = Present(r, Owner);

        r.Api.llRequestPermissions(Owner.ToString(), Debit);
        Answer(r, owner, r.Item.ItemID, Debit);

        Assert.Equal(Owner, r.Item.PermsGranter);
        Assert.Equal(Debit, r.Item.PermsMask);
        r.Api.llGiveMoney(Payee.ToString(), 5);
        Assert.Equal(5, (await Paid(r, Payee)).Amount);
    }

    [Fact]
    public void Someone_else_is_never_asked_for_debit()
    {
        // The "free demo HUD" attack: a stranger who says yes must not hand over PERMISSION_DEBIT, because a temporary
        // attach or a copy sale would later make them the owner with the grant kept.
        var r = Setup();
        var stranger = Present(r, Stranger);

        r.Api.llRequestPermissions(Stranger.ToString(), Debit | Anim);
        Answer(r, stranger, r.Item.ItemID, Debit | Anim);

        Assert.Equal(Stranger, r.Item.PermsGranter);
        Assert.Equal(Anim, r.Item.PermsMask);
    }

    [Fact]
    public void Asking_someone_else_only_for_debit_grants_nothing()
    {
        var r = Setup();
        var stranger = Present(r, Stranger);

        r.Api.llRequestPermissions(Stranger.ToString(), Debit);
        Answer(r, stranger, r.Item.ItemID, Debit);

        Assert.Equal(0, r.Item.PermsMask & Debit);
        var answer = Assert.Single(Events(r, "run_time_permissions"));
        Assert.Equal(0, ((LSL_Types.LSLInteger)answer.Params[0]).value);
    }

    [Fact]
    public void Auto_grant_gives_someone_else_the_rest_without_debit()
    {
        var r = Setup(autoGrantAll: true);

        r.Api.llRequestPermissions(Stranger.ToString(), Debit | Anim);

        Assert.Equal(Anim, r.Item.PermsMask);
        var answer = Assert.Single(Events(r, "run_time_permissions"));
        Assert.Equal(Anim, ((LSL_Types.LSLInteger)answer.Params[0]).value);
    }

    [Fact]
    public void An_owner_change_before_the_answer_keeps_debit_out()
    {
        var r = Setup();
        var owner = Present(r, Owner);

        r.Api.llRequestPermissions(Owner.ToString(), Debit);
        r.Part.OwnerID = Stranger;
        Answer(r, owner, r.Item.ItemID, Debit);

        Assert.Equal(0, r.Item.PermsMask & Debit);
    }

    [Fact]
    public void An_answer_for_another_script_in_the_prim_is_ignored()
    {
        // R-7: another script's DEBIT question must not grant this one anything.
        var r = Setup();
        var owner = Present(r, Owner);

        r.Api.llRequestPermissions(Owner.ToString(), Anim);
        Answer(r, owner, UUID.Random(), Debit);

        Assert.Equal(0, r.Item.PermsMask);

        Answer(r, owner, r.Item.ItemID, Anim);
        Assert.Equal(Anim, r.Item.PermsMask);
    }

    [Fact]
    public void An_answer_grants_no_more_than_was_asked()
    {
        var r = Setup();
        var owner = Present(r, Owner);

        r.Api.llRequestPermissions(Owner.ToString(), Anim);
        Answer(r, owner, r.Item.ItemID, Anim | Debit);

        Assert.Equal(Anim, r.Item.PermsMask);
        var answer = Assert.Single(Events(r, "run_time_permissions"));
        Assert.Equal(Anim, ((LSL_Types.LSLInteger)answer.Params[0]).value);
    }

    [Fact]
    public void A_temporary_attach_keeps_no_debit_from_before_the_owner_change()
    {
        // llAttachToAvatarTemp makes the granter the owner and keeps their grant. A PERMISSION_DEBIT granted before
        // (saved by an older OpenSim) must not start spending the new owner's money.
        var r = Setup(attachments: true);
        Present(r, Stranger);
        Grant(r, Stranger, ScriptBaseClass.PERMISSION_ATTACH | Debit);

        r.Api.llAttachToAvatarTemp(ScriptBaseClass.ATTACH_HUD_CENTER_1);

        Assert.Equal(Stranger, r.Part.ParentGroup.OwnerID);
        Assert.Equal(Stranger, r.Item.PermsGranter);
        Assert.Equal(ScriptBaseClass.PERMISSION_ATTACH, r.Item.PermsMask);
    }

    // --- A saved PERMISSION_DEBIT comes back only from a running script's own state, granted by the owner (R-1). ----

    [Theory]
    [InlineData(true, true, true)]     // a region restart or a crossing: the owner's own grant stays
    [InlineData(true, false, false)]   // granted by someone else: never usable, so it goes
    [InlineData(false, true, false)]   // a rez or an attach from an asset (an IAR, a copy sale): ask again
    [InlineData(false, false, false)]
    public void A_restored_debit_stays_only_for_the_owners_running_script(bool fromRunningScript, bool byOwner, bool kept)
    {
        var mask = DebitPermission.Restored(Debit | Anim, byOwner ? Owner : Stranger, Owner, fromRunningScript);

        Assert.Equal(kept ? Debit | Anim : Anim, mask);
    }

    [Fact]
    public void A_restore_without_debit_is_unchanged() =>
        Assert.Equal(Anim, DebitPermission.Restored(Anim, Stranger, Owner, fromRunningScript: false));

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
