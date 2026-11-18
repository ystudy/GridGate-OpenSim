using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using GridGate.Region.Money;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.ClientStack.Linden;
using OpenSim.Region.CoreModules.Agent.AssetTransaction;
using OpenSim.Region.CoreModules.Avatar.UserProfiles;
using OpenSim.Region.CoreModules.Framework.UserManagement;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using static GridGate.Money.Tests.MoneyScene;
using FlotsamConnector = OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups.IGroupsServicesConnector;
using FlotsamGroupsModule = OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups.GroupsModule;
using V2Connector = OpenSim.Groups.IGroupsServicesConnector;
using V2GroupsModule = OpenSim.Groups.GroupsModule;

namespace GridGate.Money.Tests;

/// <summary>
/// GridGate's other changes to OpenSim's money paths (money design §7.8), run against OpenSim's own modules with
/// GridGate's money module: UDP upload charging (AssetXferUploader), the classified fee (UserProfileModule), and the fees
/// held before their action: group joins (both groups modules), classifieds and caps uploads (BunchOfCaps). Money
/// review 2026-11-15, R-10.
/// </summary>
[Collection(SceneCollection.Name)]
public sealed class OpenSimMoneyPathTests
{
    private static MoneyAnswer Balance(long coins) => new("ok", coins, 0, 1, 7);

    // --- UDP uploads (commit 5cf5b706) -------------------------------------------------------------------------------

    private const int UploadFee = 10;

    private static (MoneyScene Region, AssetTransactionModule Uploads, RecordingClient Uploader) UploadRegion(long balance, int fee = UploadFee)
    {
        var uploads = new AssetTransactionModule();
        var region = Build(new EconomyInfo("C$", fee, 0, true, true, 1_000_000), others: [uploads]);
        region.Service.Answer = _ => Balance(balance);
        return (region, uploads, region.Arrive(Buyer));
    }

    private static readonly UUID UploadTransaction = TestHelpers.ParseTail(0x200);
    private static readonly UUID UploadAssetId = TestHelpers.ParseTail(0x201);

    private static void Send(RecordingClient uploader, sbyte type, bool storeLocal, bool temporary) =>
        uploader.Raise(nameof(IClientAPI.OnAssetUploadRequest), uploader, UploadAssetId, UploadTransaction,
            type, new byte[64], storeLocal, temporary);

    private static bool CreateItem(AssetTransactionModule uploads, RecordingClient uploader, sbyte createAs)
    {
        var invType = (AssetType)createAs == AssetType.Clothing ? InventoryType.Wearable
            : (AssetType)createAs == AssetType.Notecard ? InventoryType.Notecard : InventoryType.Texture;
        return uploads.HandleItemCreationFromTransaction(uploader, UploadTransaction, UUID.Zero, 1, "", "A texture",
            (sbyte)invType, createAs, 0, (uint)OpenSim.Framework.PermissionMask.All);
    }

    /// <summary>
    /// The viewer's AssetUploadRequest with the asset in it, then (unless <paramref name="create"/> is false) the
    /// CreateInventoryItem that names its transaction (which the inventory access module hands to the upload module).
    /// </summary>
    private static bool Upload(AssetTransactionModule uploads, RecordingClient uploader, sbyte type = (sbyte)AssetType.Texture,
        bool storeLocal = false, sbyte? createAs = null, bool create = true, bool temporary = false)
    {
        Send(uploader, type, storeLocal, temporary);
        return !create || CreateItem(uploads, uploader, createAs ?? type);
    }

    private static List<InventoryItemBase> Textures(MoneyScene region) =>
        region.Scene.InventoryService.GetFolderContent(Buyer, region.Scene.InventoryService.GetFolderForType(Buyer, FolderType.Texture).ID)
            .Items.Where(i => i.Name == "A texture").ToList();

    /// <summary>money review 2026-11-15, R-10: a UDP upload asks the money module whether the fee is covered, then charges it.</summary>
    [Fact]
    public void A_udp_upload_checks_the_fee_is_covered_then_charges_it()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Assert.True(Upload(uploads, uploader));

        Assert.Single(Textures(region));
        var charge = Assert.Single(region.Service.Of("charge"));
        Assert.Equal("upload_fee", charge.Args[0]);
        Assert.Equal(Buyer.Guid, ((MoneyWho)charge.Args[1]!).AgentId);
        Assert.Equal(uploader.SessionId.Guid, ((MoneyWho)charge.Args[1]!).SessionId);
        Assert.Equal((long)UploadFee, charge.Args[2]);
        lock (region.Service.Order) Assert.Equal(new[] { "balance", "charge" }, region.Service.Order);
    }

    /// <summary>money review 2026-11-15, R-10: an uploader who can't pay the fee gets no upload and pays nothing.</summary>
    [Fact]
    public void A_udp_upload_the_uploader_cannot_pay_for_is_refused_and_not_charged()
    {
        var (region, uploads, uploader) = UploadRegion(balance: UploadFee - 1);

        Upload(uploads, uploader);

        uploader.WaitForAlert("Insufficient funds");
        Assert.Empty(Textures(region));
        Assert.Empty(region.Service.Of("charge"));
    }

    /// <summary>money review 2026-11-15, R-10: a temporary upload (a viewer's local preview) is neither checked nor charged.</summary>
    [Fact]
    public void A_temporary_udp_upload_is_not_charged()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Upload(uploads, uploader, temporary: true);

        Assert.Empty(region.Service.Calls);
    }

    /// <summary>A store-only UDP upload (no item) is a way to put an asset in the store, so it pays the fee.</summary>
    [Fact]
    public void A_store_only_udp_upload_is_charged()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Upload(uploads, uploader, storeLocal: true, create: false);

        Assert.Equal("upload_fee", Assert.Single(region.Service.Of("charge")).Args[0]);
    }

    /// <summary>The fee is for the type the upload was checked as: a texture can't be saved under a type with no fee.</summary>
    [Fact]
    public void A_udp_texture_saved_as_a_notecard_is_refused_and_not_charged()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Upload(uploads, uploader, createAs: (sbyte)AssetType.Notecard);

        uploader.WaitForAlert(AssetXferUploader.MismatchText);
        Assert.Empty(Textures(region));
        Assert.Empty(region.Service.Of("charge"));
        Assert.Null(region.Scene.AssetService.Get(UploadAssetId.ToString()));
    }

    /// <summary>Viewers send some textures as TextureTGA; saving one as a texture item works and pays once.</summary>
    [Fact]
    public void A_udp_tga_texture_saved_as_a_texture_is_created_and_charged_once()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Assert.True(Upload(uploads, uploader, type: (sbyte)AssetType.TextureTGA, createAs: (sbyte)AssetType.Texture));

        Assert.Single(Textures(region));
        Assert.Single(region.Service.Of("charge"));
        Assert.Empty(uploader.Alerts);
    }

    private static InventoryItemBase AddItem(MoneyScene region, AssetType type, InventoryType invType, FolderType folder)
    {
        var item = new InventoryItemBase(UUID.Random(), Buyer)
        {
            Name = "An item", AssetType = (int)type, InvType = (int)invType,
            Folder = region.Scene.InventoryService.GetFolderForType(Buyer, folder).ID,
            BasePermissions = (uint)OpenSim.Framework.PermissionMask.All, CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All,
        };
        Assert.True(region.Scene.InventoryService.AddItem(item));
        return item;
    }

    /// <summary>An item update takes the asset from the upload, so it is charged by what was uploaded.</summary>
    [Fact]
    public void An_item_update_through_a_udp_upload_is_charged_by_the_uploaded_type()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);
        var item = AddItem(region, AssetType.Texture, InventoryType.Texture, FolderType.Texture);

        Send(uploader, (sbyte)AssetType.Texture, storeLocal: false, temporary: false);
        uploads.HandleItemUpdateFromTransaction(uploader, UploadTransaction, item);

        Assert.Single(region.Service.Of("charge"));
    }

    /// <summary>A free upload type can't be turned into a paid item by updating one with it.</summary>
    [Fact]
    public void A_notecard_upload_used_to_update_a_texture_item_is_refused_and_not_charged()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);
        var item = AddItem(region, AssetType.Texture, InventoryType.Texture, FolderType.Texture);

        Send(uploader, (sbyte)AssetType.Notecard, storeLocal: false, temporary: false);
        uploads.HandleItemUpdateFromTransaction(uploader, UploadTransaction, item);

        uploader.WaitForAlert(AssetXferUploader.MismatchText);
        Assert.Empty(region.Service.Of("charge"));
        Assert.Null(region.Scene.AssetService.Get(UploadAssetId.ToString()));
    }

    [Fact]
    public void A_notecard_upload_updating_a_notecard_item_still_works()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);
        var item = AddItem(region, AssetType.Notecard, InventoryType.Notecard, FolderType.Notecard);

        Send(uploader, (sbyte)AssetType.Notecard, storeLocal: false, temporary: false);
        uploads.HandleItemUpdateFromTransaction(uploader, UploadTransaction, item);

        Assert.NotNull(region.Scene.AssetService.Get(UploadAssetId.ToString()));
        Assert.Empty(region.Service.Of("charge"));
        Assert.Empty(uploader.Alerts);
    }

    /// <summary>Viewers can send a texture as ImageJPEG or ImageTGA; it is the same upload fee as any texture.</summary>
    [Fact]
    public void A_udp_jpeg_texture_saved_as_a_texture_is_checked_and_charged_once()
    {
        var (region, uploads, uploader) = UploadRegion(balance: UploadFee - 1);
        Upload(uploads, uploader, type: (sbyte)AssetType.ImageJPEG, createAs: (sbyte)AssetType.Texture);
        uploader.WaitForAlert("Insufficient funds");
        Assert.Empty(Textures(region));

        var (region2, uploads2, uploader2) = UploadRegion(balance: 100);
        Assert.True(Upload(uploads2, uploader2, type: (sbyte)AssetType.ImageJPEG, createAs: (sbyte)AssetType.Texture));
        Assert.Single(Textures(region2));
        Assert.Single(region2.Service.Of("charge"));
    }

    /// <summary>With no upload fee in force, OpenSim's own behaviour is unchanged: any item type may be made from an upload.</summary>
    [Fact]
    public void Without_an_upload_fee_a_texture_upload_saved_as_a_notecard_is_created_as_before()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100, fee: 0);

        Upload(uploads, uploader, createAs: (sbyte)AssetType.Notecard);

        Assert.DoesNotContain(AssetXferUploader.MismatchText, uploader.Alerts);
        Assert.NotNull(region.Scene.AssetService.Get(UploadAssetId.ToString()));
    }

    /// <summary>The same upload stored twice (store-only, then its item) pays once.</summary>
    [Fact]
    public void A_store_only_upload_followed_by_an_item_is_charged_once()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Upload(uploads, uploader, storeLocal: true, create: false);
        CreateItem(uploads, uploader, (sbyte)AssetType.Texture);

        Assert.Single(region.Service.Of("charge"));
        Assert.Single(Textures(region));
    }

    /// <summary>The viewer may name the item before the upload arrives; the fee is still taken once, when it is stored.</summary>
    [Fact]
    public void An_item_created_before_its_upload_arrives_is_charged_once()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        CreateItem(uploads, uploader, (sbyte)AssetType.Texture);
        Assert.Empty(region.Service.Of("charge"));
        Send(uploader, (sbyte)AssetType.Texture, storeLocal: false, temporary: false);

        Assert.Single(region.Service.Of("charge"));
        Assert.Single(Textures(region));
    }

    /// <summary>A wearable has no upload fee; saving one through UDP still makes its item.</summary>
    [Fact]
    public void A_wearable_saved_through_udp_creates_its_item_and_is_not_charged()
    {
        var (region, uploads, uploader) = UploadRegion(balance: 100);

        Assert.True(Upload(uploads, uploader, type: (sbyte)AssetType.Clothing, createAs: (sbyte)AssetType.Clothing));

        var clothing = region.Scene.InventoryService.GetFolderContent(Buyer,
            region.Scene.InventoryService.GetFolderForType(Buyer, FolderType.Clothing).ID).Items.Where(i => i.Name == "A texture");
        Assert.Single(clothing);
        Assert.Empty(region.Service.Of("charge"));
        Assert.Empty(uploader.Alerts);
    }

    // --- classifieds (commit 9a45df55) -------------------------------------------------------------------------------

    private const int ClassifiedFee = 25;

    /// <summary>A region with the profile module; <paramref name="feeHolds"/> false is a money service from before fee holds.</summary>
    private static (MoneyScene Region, RecordingClient Lister, UserProfileModule Profiles, ProfileServiceStub Server) ClassifiedRegion(long balance,
        bool feeHolds = false)
    {
        // OpenSim's own HTTP clients, as a region server sets them up at start; the profile module talks JSON-RPC with them.
        if (WebUtil.SharedSocketsHttpHandler is null) WebUtil.SetupHTTPClients(false, false, null, 32);
        var server = new ProfileServiceStub();
        var profiles = new UserProfileModule();
        var config = Config();
        config.AddConfig("UserProfiles").Set("ProfileServiceURL", server.Uri);
        var region = BuildWith(config, new EconomyInfo("C$", 0, ClassifiedFee, true, true, 1_000_000, feeHolds), new UserManagementModule(), profiles);
        region.Service.Answer = _ => Balance(balance);
        return (region, region.Arrive(Buyer), profiles, server);
    }

    private static MoneyScene BuildWith(Nini.Config.IConfigSource config, EconomyInfo economy, params object[] others) =>
        Build(economy, others, config: config);

    /// <summary>The viewer's ClassifiedInfoUpdate for a new ad, with the listing price the resident typed.</summary>
    private static void List(RecordingClient lister, int viewerPrice) =>
        lister.Raise(nameof(IClientAPI.OnClassifiedInfoUpdate), TestHelpers.ParseTail(0x300), 1u, "An ad", "About the ad", UUID.Zero, 1u,
            UUID.Zero, new Vector3(128, 128, 25), (byte)0, viewerPrice, lister);

    /// <summary>
    /// money review 2026-11-15, R-10: a new classified is checked against and charged the money module's fee; a listing
    /// price of 0 doesn't make it free.
    /// </summary>
    [Fact]
    public void A_classified_is_checked_and_charged_at_the_grids_fee_not_the_viewers_price()
    {
        var (region, lister, _, server) = ClassifiedRegion(balance: ClassifiedFee);
        using var _server = server;

        List(lister, viewerPrice: 0);

        Assert.Contains("classified_update", server.Methods);
        var charge = Assert.Single(region.Service.Of("charge"));
        Assert.Equal("classified_fee", charge.Args[0]);
        Assert.Equal((long)ClassifiedFee, charge.Args[2]);
        lock (region.Service.Order) Assert.Equal(new[] { "balance", "charge" }, region.Service.Order);
    }

    /// <summary>
    /// money review 2026-11-15, R-10: a resident short of the grid's fee is refused, however low the price they typed;
    /// the ad isn't saved and nothing is charged.
    /// </summary>
    [Fact]
    public void A_classified_the_lister_cannot_pay_the_grids_fee_for_is_refused()
    {
        var (region, lister, _, server) = ClassifiedRegion(balance: ClassifiedFee - 1);
        using var _server = server;

        List(lister, viewerPrice: 1);

        lister.WaitForAlert("You do not have enough money to create this classified.");
        Assert.DoesNotContain("classified_update", server.Methods);
        Assert.Empty(region.Service.Of("charge"));
    }

    // --- classifieds with fee holds ------------------------------------------------------------------------------------

    [Fact]
    public void A_classified_fee_is_held_before_the_ad_is_saved_and_completed_after()
    {
        var (region, lister, _, server) = ClassifiedRegion(balance: 100, feeHolds: true);
        using var _server = server;
        var savedBeforeHold = new ConcurrentQueue<bool>();
        region.Service.Gate = method =>
        {
            if (method == "hold") savedBeforeHold.Enqueue(server.Methods.Contains("classified_update"));
            return Task.CompletedTask;
        };

        List(lister, viewerPrice: 0);

        region.WaitFor("complete");
        Assert.Equal([false], savedBeforeHold);
        Assert.Contains("classified_update", server.Methods);
        var hold = Assert.Single(region.Service.Of("hold"));
        Assert.Equal("classified_fee", hold.Args[0]);
        Assert.Equal((long)ClassifiedFee, hold.Args[3]);
        Assert.Empty(region.Service.Of("charge"));
        Assert.Empty(region.Service.Of("return"));
    }

    [Fact]
    public void A_classified_the_profile_server_refuses_returns_the_fee()
    {
        var (region, lister, _, server) = ClassifiedRegion(balance: 100, feeHolds: true);
        using var _server = server;
        server.RefuseUpdates = true;

        List(lister, viewerPrice: 0);

        region.WaitFor("return");
        lister.WaitForAlert("Error updating classified");
        Assert.Empty(region.Service.Of("complete"));
        Assert.Empty(region.Service.Of("charge"));
    }

    [Fact]
    public void A_classified_the_lister_cannot_pay_is_not_saved()
    {
        var (region, lister, _, server) = ClassifiedRegion(balance: 100, feeHolds: true);
        using var _server = server;
        region.Service.Queue("hold", new MoneyAnswer("not_enough"));

        List(lister, viewerPrice: 0);

        lister.WaitForAlert("You do not have enough money to create this classified.");
        Assert.DoesNotContain("classified_update", server.Methods);
        Assert.Empty(region.Service.Of("complete"));
        Assert.Empty(region.Service.Of("return"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Editing_an_existing_classified_holds_nothing(bool feeHolds)
    {
        var (region, lister, _, server) = ClassifiedRegion(balance: 0, feeHolds: feeHolds);
        using var _server = server;
        server.Existing = TestHelpers.ParseTail(0x300);

        List(lister, viewerPrice: 0);

        Assert.Contains("classified_update", server.Methods);
        Assert.Empty(region.Service.Of("hold"));
        Assert.Empty(region.Service.Of("charge"));
        Assert.Empty(region.Service.Of("balance"));
        Assert.Empty(lister.Alerts);
    }

    // --- group joins ---------------------------------------------------------------------------------------------------

    private const int JoinFee = 20;
    private const string V2 = "Groups Module V2", Flotsam = "GroupsModule";
    private static readonly UUID InviteId = TestHelpers.ParseTail(0x400);

    private sealed record GroupsRig(MoneyScene Region, IGroupsModule Groups, GroupsService Service, RecordingClient Joiner,
        ConcurrentQueue<GridInstantMessage> Sent)
    {
        /// <summary>The invitation the viewer answers: the one <see cref="Invite"/> sent, else a fixed id no module knows.</summary>
        public Guid InviteGuid { get; set; } = InviteId.Guid;
    }

    /// <summary>
    /// A region with OpenSim's own groups module (<see cref="V2"/> or <see cref="Flotsam"/>, chosen as the INI does) over a
    /// faked groups service whose one group, "Club", costs <see cref="JoinFee"/> to join and was founded by <see cref="MoneyScene.Founder"/>.
    /// </summary>
    private static GroupsRig GroupsRegion(string module, bool feeHolds = true, long balance = 100)
    {
        var config = Config();
        var groups = config.AddConfig("Groups");
        groups.Set("Enabled", "true");
        groups.Set("Module", module);
        var service = new GroupsService();
        var sent = new ConcurrentQueue<GridInstantMessage>();
        IGroupsModule groupsModule = module == V2 ? new V2GroupsModule() : new FlotsamGroupsModule();
        var region = Build(new EconomyInfo("C$", 0, 0, true, true, 1_000_000, feeHolds), config: config, beforeMoney: scene =>
        {
            if (module == V2) scene.RegisterModuleInterface(Stand.Create<V2Connector>(service.Order, service.Answer));
            else scene.RegisterModuleInterface(Stand.Create<FlotsamConnector>(service.Order, service.Answer));
            scene.RegisterModuleInterface(Stand.Create<IMessageTransferModule>(new ConcurrentQueue<string>(), (m, args) =>
            {
                if (m.Name == nameof(IMessageTransferModule.SendInstantMessage)) sent.Enqueue((GridInstantMessage)args[0]!);
                return (false, null);
            }));
            SceneHelpers.SetupSceneModules(scene, config, new UserManagementModule(), groupsModule);
        });
        region.Service.Answer = _ => Balance(balance);
        return new GroupsRig(region, groupsModule, service, region.Arrive(Buyer), sent);
    }

    /// <summary>The joiner invites <see cref="MoneyScene.Stranger"/> (not in the region) to "Club"; the invitation sent.</summary>
    private static GridInstantMessage Invite(GroupsRig g)
    {
        g.Groups.InviteGroupRequest(g.Joiner, GroupId, Stranger, GroupsService.InviteRole);
        var sent = Assert.Single(g.Sent);
        g.InviteGuid = sent.imSessionID;
        Assert.Equal((byte)InstantMessageDialog.GroupInvitation, sent.dialog);
        return sent;
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void An_invitation_to_a_paid_group_says_the_fee_and_sends_it_to_the_viewer(string module)
    {
        var g = GroupsRegion(module);

        var sent = Invite(g);

        Assert.DoesNotContain("no cost", sent.message, StringComparison.Ordinal);
        Assert.Contains($"There is a fee of {JoinFee} to join this group.", sent.message, StringComparison.Ordinal);
        // What viewers read: the fee as a 4-byte signed number, high byte first, then the role the invitation gives.
        Assert.Equal(20, sent.binaryBucket.Length);
        Assert.Equal(new byte[] { 0, 0, 0, JoinFee }, sent.binaryBucket[..4]);
        Assert.Equal(GroupsService.InviteRole, new UUID(sent.binaryBucket, 4));
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void An_invitation_to_a_free_group_still_says_there_is_no_cost(string module)
    {
        var g = GroupsRegion(module);
        g.Service.Fee = 0;

        var sent = Invite(g);

        Assert.EndsWith("There is no cost to join this group.", sent.message, StringComparison.Ordinal);
        Assert.Equal(new byte[20], sent.binaryBucket);
    }

    /// <summary>The viewer accepting an invitation to "Club", as an instant message.</summary>
    private static void AcceptInvitation(GroupsRig g, InstantMessageDialog answer = InstantMessageDialog.GroupInvitationAccept, RecordingClient? client = null) =>
        (client ?? g.Joiner).Raise(nameof(IClientAPI.OnInstantMessage), client ?? g.Joiner, new GridInstantMessage
        {
            dialog = (byte)answer, imSessionID = g.InviteGuid, fromAgentID = Buyer.Guid,
            toAgentID = GroupId.Guid, message = "", fromAgentName = "", binaryBucket = [],
        });

    private static void AssertHeldThenCompleted(GroupsRig g, ConcurrentQueue<bool> addedBeforeHold)
    {
        g.Region.WaitFor("complete");
        Assert.Equal([false], addedBeforeHold);
        var hold = Assert.Single(g.Region.Service.Of("hold"));
        Assert.Equal("group_join_fee", hold.Args[0]);
        Assert.Equal(Founder.Guid, ((MoneyWho)hold.Args[2]!).AgentId);
        Assert.Equal((long)JoinFee, hold.Args[3]);
        Assert.Empty(g.Region.Service.Of("return"));
        Assert.Empty(g.Region.Service.Of("charge"));
        Assert.Contains(Buyer, g.Service.Members);
    }

    private static ConcurrentQueue<bool> AddedBeforeHold(GroupsRig g)
    {
        var added = new ConcurrentQueue<bool>();
        g.Region.Service.Gate = method =>
        {
            if (method == "hold") added.Enqueue(g.Service.Order.Contains("AddAgentToGroup"));
            return Task.CompletedTask;
        };
        return added;
    }

    [Fact]
    public void A_group_join_holds_the_fee_before_adding_the_member_and_completes_after()
    {
        var g = GroupsRegion(V2);
        var added = AddedBeforeHold(g);

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        AssertHeldThenCompleted(g, added);
    }

    [Fact]
    public void A_group_join_the_groups_service_refuses_returns_the_fee()
    {
        var g = GroupsRegion(V2);
        g.Service.Refuse = true;

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        g.Region.WaitFor("return");
        Assert.Empty(g.Region.Service.Of("complete"));
        Assert.Empty(g.Region.Service.Of("charge"));
    }

    [Fact]
    public void A_group_join_the_joiner_cannot_pay_never_adds_them()
    {
        var g = GroupsRegion(V2);
        g.Region.Service.Queue("hold", new MoneyAnswer("not_enough"));

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        g.Joiner.WaitForAlert("Insufficient funds to join the group.");
        Assert.DoesNotContain("AddAgentToGroup", g.Service.Order);
        Assert.Empty(g.Region.Service.Of("charge"));
    }

    [Fact]
    public void Accepting_a_group_invitation_holds_and_completes_the_join_fee()
    {
        var g = GroupsRegion(V2);
        var added = AddedBeforeHold(g);

        AcceptInvitation(g);

        AssertHeldThenCompleted(g, added);
    }

    // --- join fees follow the invitation (both groups modules) ------------------------------------------------------

    private static void AssertNoHoldAndNotAdded(GroupsRig g)
    {
        Assert.Empty(g.Region.Service.Of("hold"));
        Assert.Empty(g.Region.Service.Of("charge"));
        Assert.DoesNotContain(Buyer, g.Service.Members);
    }

    /// <summary>The one fresh invitation the invitee was sent.</summary>
    private static GridInstantMessage FreshInvitation(GroupsRig g) =>
        Assert.Single(g.Joiner.Instant, m => m.dialog == (byte)InstantMessageDialog.GroupInvitation);

    private static void AssertOffersFee(GroupsRig g, GridInstantMessage invitation, int fee)
    {
        Assert.Equal(g.InviteGuid, invitation.imSessionID);
        Assert.Equal(GroupId.Guid, invitation.fromAgentID);
        Assert.Equal("Club", invitation.fromAgentName);
        Assert.Equal($"The fee to join Club is now {fee}. There is a fee of {fee} to join this group.", invitation.message);
        Assert.Equal(20, invitation.binaryBucket.Length);
        Assert.Equal(fee, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(invitation.binaryBucket));
        Assert.Equal(GroupsService.InviteRole, new UUID(invitation.binaryBucket, 4));
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void A_paid_invitation_accepted_after_the_fee_went_up_adds_nobody_and_offers_the_new_fee(string module)
    {
        var g = GroupsRegion(module, balance: 100000);
        Invite(g);
        g.Service.Fee = 5000;

        AcceptInvitation(g);

        AssertNoHoldAndNotAdded(g);
        AssertOffersFee(g, FreshInvitation(g), 5000);
        Assert.DoesNotContain("RemoveAgentToGroupInvite", g.Service.Order);

        // Accepting the fresh invitation agrees to the new fee.
        AcceptInvitation(g);

        g.Region.WaitFor("complete");
        var hold = Assert.Single(g.Region.Service.Of("hold"));
        Assert.Equal((long)5000, hold.Args[3]);
        Assert.Contains(Buyer, g.Service.Members);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void A_paid_invitation_accepted_after_the_fee_went_down_charges_the_lower_fee(string module)
    {
        var g = GroupsRegion(module);
        Invite(g);
        g.Service.Fee = 5;

        AcceptInvitation(g);

        g.Region.WaitFor("complete");
        var hold = Assert.Single(g.Region.Service.Of("hold"));
        Assert.Equal((long)5, hold.Args[3]);
        Assert.Contains(Buyer, g.Service.Members);
        Assert.DoesNotContain(g.Joiner.Instant, m => m.dialog == (byte)InstantMessageDialog.GroupInvitation);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void An_invitation_with_no_known_fee_to_a_paid_group_offers_the_fee_first(string module)
    {
        var g = GroupsRegion(module);
        g.Service.StoredFee = null; // as an older region stored it; this module never offered it either

        AcceptInvitation(g);

        AssertNoHoldAndNotAdded(g);
        AssertOffersFee(g, FreshInvitation(g), JoinFee);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void An_invitation_made_free_charges_nothing(string module)
    {
        var g = GroupsRegion(module);
        Invite(g);
        g.Service.Fee = 0;

        AcceptInvitation(g);

        Assert.Empty(g.Region.Service.Of("hold"));
        Assert.Empty(g.Region.Service.Of("charge"));
        Assert.Contains(Buyer, g.Service.Members);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void A_free_invitation_to_a_group_that_became_paid_offers_the_fee_first(string module)
    {
        var g = GroupsRegion(module);
        g.Service.Fee = 0;
        Invite(g);
        g.Service.Fee = JoinFee;

        AcceptInvitation(g);

        AssertNoHoldAndNotAdded(g);
        AssertOffersFee(g, FreshInvitation(g), JoinFee);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void A_member_accepting_an_old_invitation_pays_nothing(string module)
    {
        var g = GroupsRegion(module);
        Invite(g);
        g.Groups.JoinGroupRequest(g.Joiner, GroupId);
        g.Region.WaitFor("complete");

        AcceptInvitation(g);

        Assert.Single(g.Region.Service.Of("hold"));
        Assert.Empty(g.Region.Service.Of("charge"));
        Assert.Contains("RemoveAgentToGroupInvite", g.Service.Order);
        Assert.Contains("You are already a member of Club.", g.Joiner.Alerts);
        Assert.Empty(g.Joiner.Instant);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void Joining_twice_charges_once(string module)
    {
        var g = GroupsRegion(module);

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);
        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        g.Region.WaitFor("complete");
        Assert.Single(g.Region.Service.Of("hold"));
        Assert.Single(g.Region.Service.Of("complete"));
        Assert.Empty(g.Region.Service.Of("charge"));
        Assert.Equal([(GroupId, true), (GroupId, true)], g.Joiner.JoinReplies);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void Declining_an_invitation_forgets_the_fee_it_was_re_offered_at(string module)
    {
        var g = GroupsRegion(module, balance: 100000);
        g.Service.StoredFee = null;
        AcceptInvitation(g);
        FreshInvitation(g);

        AcceptInvitation(g, InstantMessageDialog.GroupInvitationDecline);
        AcceptInvitation(g);

        // Had the offer been kept, this accept would have charged it.
        AssertNoHoldAndNotAdded(g);
        var invitations = g.Joiner.Instant.Where(m => m.dialog == (byte)InstantMessageDialog.GroupInvitation).ToList();
        Assert.Equal(2, invitations.Count);
        AssertOffersFee(g, invitations[1], JoinFee);
    }

    // --- one paid join at a time per person and group ----------------------------------------------------------------

    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="first"/> on another thread and returns once it is waiting inside the groups service's add;
    /// the returned action lets it go and waits for it to finish.
    /// </summary>
    private static Action StartJoinHeldAtTheAdd(GroupsRig g, Action first)
    {
        using var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        g.Service.BeforeAdd = () =>
        {
            entered.Set();
            Assert.True(release.Wait(GateWait), "the test never let the first add go");
        };
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { first(); }
            catch (Exception e) { failure = e; }
        });
        thread.Start();
        SpinWait.SpinUntil(() => entered.IsSet || !thread.IsAlive, GateWait);
        if (failure != null) throw new InvalidOperationException("the first join failed", failure);
        Assert.True(entered.IsSet, "the first join never reached the groups service's add");
        return () =>
        {
            release.Set();
            Assert.True(thread.Join(GateWait), "the first join never finished");
            if (failure != null) throw new InvalidOperationException("the first join failed", failure);
            release.Dispose();
        };
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void A_second_join_while_the_first_is_adding_the_member_holds_nothing_and_adds_nobody(string module)
    {
        var g = GroupsRegion(module);
        var finishFirst = StartJoinHeldAtTheAdd(g, () => g.Groups.JoinGroupRequest(g.Joiner, GroupId));

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        // Turned away without a word of its own while the first one is still going: the first one answers.
        Assert.Empty(g.Joiner.JoinReplies);
        Assert.Empty(g.Joiner.Alerts);
        Assert.Single(g.Region.Service.Of("hold"));
        Assert.Single(g.Service.Order, c => c == "AddAgentToGroup");

        finishFirst();

        g.Region.WaitFor("complete");
        Assert.Single(g.Region.Service.Of("hold"));
        Assert.Single(g.Region.Service.Of("complete"));
        Assert.Empty(g.Region.Service.Of("return"));
        Assert.Empty(g.Region.Service.Of("charge"));
        Assert.Contains(Buyer, g.Service.Members);
        Assert.Equal([(GroupId, true)], g.Joiner.JoinReplies);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void A_second_accept_while_the_first_is_adding_the_member_holds_nothing_and_adds_nobody(string module)
    {
        var g = GroupsRegion(module);
        Invite(g);
        var finishFirst = StartJoinHeldAtTheAdd(g, () => AcceptInvitation(g));

        AcceptInvitation(g);

        // The first accept is answering: this one only says the request is still being handled.
        Assert.Equal(["Your request to join Club is still being handled."], g.Joiner.Alerts);
        Assert.Empty(g.Joiner.Instant);
        Assert.Single(g.Region.Service.Of("hold"));
        Assert.Single(g.Service.Order, c => c == "AddAgentToGroup");

        finishFirst();

        g.Region.WaitFor("complete");
        Assert.Single(g.Region.Service.Of("hold"));
        Assert.Single(g.Region.Service.Of("complete"));
        Assert.Empty(g.Region.Service.Of("return"));
        Assert.Empty(g.Region.Service.Of("charge"));
        Assert.Contains(Buyer, g.Service.Members);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void An_accept_while_a_join_is_adding_the_member_holds_nothing(string module)
    {
        var g = GroupsRegion(module);
        Invite(g);
        var finishFirst = StartJoinHeldAtTheAdd(g, () => g.Groups.JoinGroupRequest(g.Joiner, GroupId));

        AcceptInvitation(g);

        Assert.Single(g.Region.Service.Of("hold"));
        Assert.Single(g.Service.Order, c => c == "AddAgentToGroup");

        finishFirst();

        g.Region.WaitFor("complete");
        Assert.Single(g.Region.Service.Of("hold"));
        Assert.Empty(g.Region.Service.Of("charge"));
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void An_accept_sent_by_someone_else_with_the_invitees_id_forged_changes_nothing(string module)
    {
        var g = GroupsRegion(module, balance: 100000);
        Invite(g);
        g.Service.Fee = 5000;
        var forger = g.Region.Arrive(Founder);

        // The forger's client sends the accept with the invitee's id as the sender.
        AcceptInvitation(g, client: forger);

        AssertNoHoldAndNotAdded(g);
        Assert.Empty(g.Joiner.Instant);
        Assert.Empty(forger.Instant);
        Assert.DoesNotContain("RemoveAgentToGroupInvite", g.Service.Order);

        // Nothing was remembered: the real accept offers the new fee, as it would have without the forgery.
        g.Service.Fee = JoinFee;
        AcceptInvitation(g);
        g.Region.WaitFor("complete");
        Assert.Equal((long)JoinFee, Assert.Single(g.Region.Service.Of("hold")).Args[3]);
        Assert.Contains(Buyer, g.Service.Members);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void A_join_that_failed_lets_the_next_one_go_ahead(string module)
    {
        var g = GroupsRegion(module);
        g.Service.Refuse = true;
        g.Groups.JoinGroupRequest(g.Joiner, GroupId);
        g.Region.WaitFor("return");
        g.Service.Refuse = false;

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        g.Region.WaitFor("complete");
        Assert.Contains(Buyer, g.Service.Members);
        Assert.Equal([(GroupId, false), (GroupId, true)], g.Joiner.JoinReplies);
    }

    [Fact]
    public void Flotsam_join_holds_the_fee_before_adding_the_member()
    {
        var g = GroupsRegion(Flotsam);
        var added = AddedBeforeHold(g);

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        AssertHeldThenCompleted(g, added);
    }

    [Fact]
    public void Flotsam_join_the_joiner_cannot_pay_never_adds_them()
    {
        var g = GroupsRegion(Flotsam);
        g.Region.Service.Queue("hold", new MoneyAnswer("not_enough"));

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        Assert.DoesNotContain("AddAgentToGroup", g.Service.Order);
        Assert.Empty(g.Region.Service.Of("charge"));
        Assert.Empty(g.Service.Members);
    }

    [Fact]
    public void Flotsam_join_that_did_not_add_the_member_returns_the_fee()
    {
        var g = GroupsRegion(Flotsam);
        g.Service.Refuse = true;

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        g.Region.WaitFor("return");
        Assert.Contains("AddAgentToGroup", g.Service.Order);
        Assert.Empty(g.Region.Service.Of("complete"));
    }

    [Fact]
    public void Flotsam_invitation_accept_holds_the_join_fee()
    {
        var g = GroupsRegion(Flotsam);
        Invite(g);
        var added = AddedBeforeHold(g);

        AcceptInvitation(g);

        AssertHeldThenCompleted(g, added);
    }

    [Theory]
    [InlineData(V2)]
    [InlineData(Flotsam)]
    public void Without_fee_holds_on_the_service_a_join_is_checked_then_charged_after(string module)
    {
        var g = GroupsRegion(module, feeHolds: false);

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        lock (g.Region.Service.Order) Assert.Equal(new[] { "balance", "charge" }, g.Region.Service.Order);
        var charge = Assert.Single(g.Region.Service.Of("charge"));
        Assert.Equal("group_join_fee", charge.Args[0]);
        Assert.Equal(Founder.Guid, charge.Args[3]);
        Assert.Contains(Buyer, g.Service.Members);
    }

    [Fact]
    public void A_stock_money_module_is_still_checked_then_charged_after_a_join()
    {
        var g = GroupsRegion(V2);
        g.Region.Scene.UnregisterModuleInterface<IMoneyModule>(g.Region.Module);
        g.Region.Scene.RegisterModuleInterface(Stand.Create<IMoneyModule>(g.Service.Order,
            (m, _) => m.Name is nameof(IMoneyModule.AmountCovered) or nameof(IMoneyModule.UploadCovered) ? (true, true) : (false, null)));

        g.Groups.JoinGroupRequest(g.Joiner, GroupId);

        Assert.Equal(["GetGroupRecord", nameof(IMoneyModule.AmountCovered), "AddAgentToGroup", nameof(IMoneyModule.ApplyCharge)],
            g.Service.Order.Where(c => c is "GetGroupRecord" or nameof(IMoneyModule.AmountCovered) or "AddAgentToGroup" or nameof(IMoneyModule.ApplyCharge)));
        Assert.Empty(g.Region.Service.Calls);
    }

    // --- caps (HTTP) uploads -------------------------------------------------------------------------------------------

    private static readonly UUID CapsAssetId = TestHelpers.ParseTail(0x500);
    private static readonly UUID CapsItemId = TestHelpers.ParseTail(0x501);

    // OpenSim's caps need a main HTTP server (a static); it is never started.
    private static readonly System.Lazy<BaseHttpServer> CapsHttp = new(() =>
    {
        var server = new BaseHttpServer(99998);
        MainServer.AddHttpServer(server);
        return server;
    });

    /// <summary>A region whose uploader has caps, as <c>CapabilitiesModule</c> and <c>BunchOfCapsModule</c> make them.</summary>
    private static (MoneyScene Region, BunchOfCaps Caps) CapsRegion(bool feeHolds = true, long balance = 100)
    {
        var region = Build(new EconomyInfo("C$", UploadFee, 0, true, true, 1_000_000, feeHolds));
        region.Service.Answer = _ => Balance(balance);
        region.Arrive(Buyer);
        var previous = MainServer.Instance;
        MainServer.Instance = CapsHttp.Value;
        try
        {
            var caps = new Caps(MainServer.Instance, "localhost", MainServer.Instance.Port, "/" + UUID.Random() + "/", Buyer, region.Scene.RegionInfo.RegionName);
            return (region, new BunchOfCaps(region.Scene, Buyer, caps, new BunchOfCapsConfigOptions { ModelCost = new ModelCost(region.Scene) }));
        }
        finally
        {
            if (previous is not null) MainServer.Instance = previous;
        }
    }

    /// <summary>The upload's data has arrived: what the caps uploader does with it. Returns the error it gives the viewer.</summary>
    private static string CapsUpload(MoneyScene region, BunchOfCaps caps, string inventoryType = "sound", string assetType = "sound", int cost = UploadFee)
    {
        var error = "";
        int next = 0, group = 0, everyone = 0;
        var folder = region.Scene.InventoryService.GetFolderForType(Buyer, FolderType.Sound).ID;
        caps.UploadCompleteHandler("A sound", "", CapsAssetId, CapsItemId, folder, new byte[64], inventoryType, assetType, cost,
            UUID.Zero, 0, 0, 0, false, ref error, ref next, ref group, ref everyone, null);
        return error;
    }

    [Fact]
    public void A_caps_upload_holds_the_fee_and_completes_once_stored()
    {
        var (region, caps) = CapsRegion();
        var storedBeforeHold = new ConcurrentQueue<bool>();
        region.Service.Gate = method =>
        {
            if (method == "hold") storedBeforeHold.Enqueue(region.Scene.AssetService.Get(CapsAssetId.ToString()) is not null);
            return Task.CompletedTask;
        };

        Assert.Equal("", CapsUpload(region, caps));

        region.WaitFor("complete");
        Assert.Equal([false], storedBeforeHold);
        var hold = Assert.Single(region.Service.Of("hold"));
        Assert.Equal("upload_fee", hold.Args[0]);
        Assert.Null(hold.Args[2]);
        Assert.Equal((long)UploadFee, hold.Args[3]);
        Assert.Empty(region.Service.Of("charge"));
        Assert.NotNull(region.Scene.InventoryService.GetItem(Buyer, CapsItemId));
    }

    [Fact]
    public void A_caps_upload_that_fails_after_the_hold_returns_it()
    {
        var (region, caps) = CapsRegion();

        CapsUpload(region, caps, inventoryType: "object", assetType: "not a mesh");

        region.WaitFor("return");
        Assert.Empty(region.Service.Of("complete"));
        Assert.Empty(region.Service.Of("charge"));
        Assert.Null(region.Scene.InventoryService.GetItem(Buyer, CapsItemId));
    }

    [Fact]
    public void A_caps_upload_the_uploader_cannot_pay_for_is_not_stored()
    {
        var (region, caps) = CapsRegion();
        region.Service.Queue("hold", new MoneyAnswer("not_enough"));

        Assert.Equal("Insufficient funds.", CapsUpload(region, caps));

        Assert.Null(region.Scene.AssetService.Get(CapsAssetId.ToString()));
        Assert.Null(region.Scene.InventoryService.GetItem(Buyer, CapsItemId));
        Assert.Empty(region.Service.Of("charge"));
    }

    [Fact]
    public void Without_fee_holds_on_the_service_a_caps_upload_is_checked_then_charged_once()
    {
        var (region, caps) = CapsRegion(feeHolds: false);

        Assert.Equal("", CapsUpload(region, caps));

        Assert.NotNull(region.Scene.InventoryService.GetItem(Buyer, CapsItemId));
        lock (region.Service.Order) Assert.Equal(new[] { "balance", "charge" }, region.Service.Order);
        Assert.Equal((long)UploadFee, Assert.Single(region.Service.Of("charge")).Args[2]);
    }
}

/// <summary>
/// The groups service, faked behind either groups module's connector (through <see cref="Stand"/>): one group, "Club",
/// that costs <c>JoinFee</c> to join, founded by <see cref="MoneyScene.Founder"/>, with one invitation to it for
/// <see cref="MoneyScene.Buyer"/>. Every call is recorded in <see cref="Order"/>.
/// </summary>
internal sealed class GroupsService
{
    public ConcurrentQueue<string> Order { get; } = new();

    public ConcurrentDictionary<UUID, bool> MembersById { get; } = new();

    public IEnumerable<UUID> Members => MembersById.Keys;

    /// <summary>The role an invitation to "Club" gives.</summary>
    public static readonly UUID InviteRole = TestHelpers.ParseTail(0x401);

    /// <summary>What joining "Club" costs.</summary>
    public int Fee { get; set; } = 20;

    /// <summary>
    /// The fee the invitation to "Club" holds, as the invitation stored it: 20 until an invitation is made (which stores
    /// the fee the module passed); null when an older region stored none.
    /// </summary>
    public int? StoredFee { get; set; } = 20;

    /// <summary>Runs inside the service just before it adds a member: a test holds an add here to overlap two joins.</summary>
    public Action? BeforeAdd { get; set; }

    /// <summary>When set, the service adds nobody: the V2 connector says no, Flotsam's adds no membership.</summary>
    public bool Refuse { get; set; }

    private static UUID Id(object? arg) => UUID.Parse(arg!.ToString()!);

    private static object Make(Type type, params (string Field, object Value)[] fields)
    {
        var made = Activator.CreateInstance(type)!;
        foreach (var (name, value) in fields)
        {
            var field = type.GetField(name) ?? throw new InvalidOperationException($"{type.Name} has no {name}.");
            field.SetValue(made, field.FieldType == typeof(string) ? value.ToString() : value);
        }
        return made;
    }

    public (bool Handled, object? Value) Answer(MethodInfo method, object?[] args)
    {
        switch (method.Name)
        {
            case "GetGroupRecord":
                return (true, Id(args[1]) == MoneyScene.GroupId || args[2] as string == "Club"
                    ? Make(method.ReturnType, ("GroupID", MoneyScene.GroupId), ("GroupName", "Club"), ("MembershipFee", Fee), ("FounderID", MoneyScene.Founder))
                    : null);
            case "AddAgentToGroup":
                BeforeAdd?.Invoke();
                if (!Refuse) MembersById[Id(args[1])] = true;
                if (method.ReturnType != typeof(bool)) return (true, null);
                args[5] = Refuse ? "refused" : "";
                return (true, !Refuse);
            case "AddAgentToGroupInvite":
                if (args.Length > 5) StoredFee = (int?)args[5];
                return (true, method.ReturnType == typeof(bool) ? true : null);
            case "GetAgentGroupMembership":
                return (true, MembersById.ContainsKey(Id(args[1])) ? Make(method.ReturnType, ("GroupID", MoneyScene.GroupId), ("GroupName", "Club")) : null);
            case "GetAgentToGroupInvite":
                var invite = Make(method.ReturnType, ("GroupID", MoneyScene.GroupId), ("RoleID", InviteRole), ("AgentID", MoneyScene.Buyer), ("InviteID", Id(args[1])));
                // Only the V2 invitation record carries a fee.
                if (StoredFee is { } stored && method.ReturnType.GetField("Fee") is { } feeField) feeField.SetValue(invite, stored);
                return (true, invite);
            default:
                return (false, null);
        }
    }
}

/// <summary>
/// A stand-in for one of OpenSim's interfaces: each call is recorded by name, answered by the given function when it
/// handles it, and otherwise with an empty value (nothing, zero, false, an empty list).
/// </summary>
internal class Stand : DispatchProxy
{
    private ConcurrentQueue<string> _calls = new();
    private Func<MethodInfo, object?[], (bool Handled, object? Value)> _answer = (_, _) => (false, null);

    public static T Create<T>(ConcurrentQueue<string> calls, Func<MethodInfo, object?[], (bool Handled, object? Value)> answer) where T : class
    {
        var proxy = Create<T, Stand>();
        var self = (Stand)(object)proxy;
        (self._calls, self._answer) = (calls, answer);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        _calls.Enqueue(method.Name);
        var (handled, value) = _answer(method, args ?? []);
        if (handled) return value;
        var type = method.ReturnType;
        if (type == typeof(void)) return null;
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>
/// The profile server, faked: answers OpenSim's JSON-RPC calls over plain HTTP on a loopback port. The resident has no
/// classifieds yet unless <see cref="Existing"/> names one, and every update is accepted unless <see cref="RefuseUpdates"/>.
/// </summary>
internal sealed class ProfileServiceStub : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public ConcurrentQueue<string> Methods { get; } = new();

    /// <summary>When set, <c>classified_update</c> answers with an error: the ad isn't saved.</summary>
    public bool RefuseUpdates { get; set; }

    /// <summary>A classified the resident already has, listed in their classifieds.</summary>
    public UUID? Existing { get; set; }

    public string Uri { get; }

    public ProfileServiceStub()
    {
        _listener.Start();
        Uri = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var head = new StringBuilder();
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var b = stream.ReadByte();
            if (b < 0) return;
            head.Append((char)b);
        }
        var length = head.ToString().Split("\r\n")
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim()))
            .FirstOrDefault();
        var body = new byte[length];
        for (var read = 0; read < length;)
        {
            var n = stream.Read(body, read, length - read);
            if (n <= 0) return;
            read += n;
        }

        var request = (OSDMap)OSDParser.DeserializeJson(Encoding.UTF8.GetString(body));
        var method = request["method"].AsString();
        Methods.Enqueue(method);
        OSD result = method == "avatarclassifiedsrequest"
            ? Existing is { } ad ? new OSDArray { new OSDMap { ["classifieduuid"] = OSD.FromUUID(ad), ["name"] = "An ad" } } : new OSDArray()
            : request["params"];
        var reply = new OSDMap { ["jsonrpc"] = "2.0", ["id"] = request["id"] };
        if (RefuseUpdates && method == "classified_update") reply["error"] = new OSDMap { ["code"] = -32603, ["message"] = "refused" };
        else reply["result"] = result;
        var answer = OSDParser.SerializeJsonToBytes(reply);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json-rpc\r\nContent-Length: {answer.Length}\r\nConnection: close\r\n\r\n");
        stream.Write(header);
        stream.Write(answer);
    }

    public void Dispose() => _listener.Stop();
}
