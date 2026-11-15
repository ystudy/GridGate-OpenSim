using GridGate.Region.Money;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Land;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;
using static GridGate.Money.Tests.MoneyScene;

namespace GridGate.Money.Tests;

/// <summary>
/// The module's glue on a real test region: what it takes from OpenSim's land module, clients and objects before it asks
/// the money service (money review 2026-11-15, R-10).
/// </summary>
[Collection(SceneCollection.Name)]
public sealed class ModuleSceneTests
{
    private const int Gift = (int)MoneyTransactionType.Gift;
    private const int PayObject = (int)MoneyTransactionType.PayObject;

    private static readonly MoneyAnswer Rich = new("ok", 1_000, 0, 1, 7);

    // --- land: OnValidateLandBuy and OnLandBuy with OpenSim's LandManagementModule ------------------------------------

    private sealed record Land(MoneyScene Region, LandManagementModule Lmm, ILandObject Parcel, RecordingClient Buyer);

    /// <summary>One parcel over the whole region, owned by <see cref="MoneyScene.Seller"/> and for sale at <paramref name="price"/>.</summary>
    private static Land LandForSale(int price, Action<Scene>? beforeMoney = null)
    {
        var lmm = new LandManagementModule();
        var region = Build(others: [lmm], beforeMoney: beforeMoney);
        region.Scene.loadAllLandObjectsFromStorage(region.Scene.RegionInfo.RegionID);
        var parcel = lmm.GetLandObject(128, 128);
        parcel.LandData.OwnerID = Seller;
        parcel.LandData.SalePrice = price;
        parcel.LandData.Flags |= (uint)ParcelFlags.ForSale;
        region.Service.Answer = _ => Rich;
        return new Land(region, lmm, parcel, region.Arrive(Buyer));
    }

    /// <summary>The viewer's ParcelBuy, as the client stack hands it to the scene.</summary>
    private static void Buy(Land l, int price, bool forGroup = false) =>
        l.Region.Scene.ProcessParcelBuy(Buyer, forGroup ? GroupId : UUID.Zero, true, forGroup, false, l.Parcel.LandData.LocalID,
            l.Parcel.LandData.Area, price, false);

    private static LandData Now(Land l) => l.Lmm.GetLandObject(l.Parcel.LandData.LocalID).LandData;

    /// <summary>money review 2026-11-15, R-10: a land purchase holds the price, the land changes hands, then the hold completes.</summary>
    [Fact]
    public void Buying_land_holds_the_price_and_completes_once_the_buyer_owns_it()
    {
        var l = LandForSale(100);

        Buy(l, 100);

        var hold = Assert.Single(l.Region.Service.Of("hold"));
        Assert.Equal("land", hold.Args[0]);
        Assert.Equal(Buyer.Guid, ((MoneyWho)hold.Args[1]!).AgentId);
        Assert.Equal(l.Buyer.SessionId.Guid, ((MoneyWho)hold.Args[1]!).SessionId);
        Assert.Equal(Seller.Guid, ((MoneyWho)hold.Args[2]!).AgentId);
        Assert.Equal(100L, hold.Args[3]);
        Assert.Equal(Buyer, Now(l).OwnerID);
        Assert.Equal(7L, l.Region.WaitFor("complete").Args[0]);
        Assert.Empty(l.Region.Service.Of("return"));
    }

    /// <summary>
    /// money review 2026-11-15, R-10: the land module accepts any offer of at least the sale price; GridGate takes exactly
    /// the sale price, so a higher offer is refused and the land stays the seller's.
    /// </summary>
    [Fact]
    public void An_offer_above_the_sale_price_is_refused_and_the_land_stays_the_sellers()
    {
        var l = LandForSale(100);

        Buy(l, 150);

        Assert.Empty(l.Region.Service.Calls);
        Assert.Equal(Seller, Now(l).OwnerID);
        l.Buyer.WaitForAlert(MoneyRules.Message(Refusal.PriceChanged));
    }

    /// <summary>money review 2026-11-15, R-10: an owner other than the one the land module validated is refused.</summary>
    [Fact]
    public void A_parcel_whose_owner_changed_after_the_land_module_checked_it_is_refused()
    {
        // Hooked after the land module's validation and before the money module's: the parcel changes hands in between.
        var l = LandForSale(100, beforeMoney: scene => scene.EventManager.OnValidateLandBuy += (_, e) =>
        {
            if (e.landValidated) scene.LandChannel.GetLandObject(e.parcelLocalID).LandData.OwnerID = Stranger;
        });

        Buy(l, 100);

        Assert.Empty(l.Region.Service.Of("hold"));
        Assert.NotEqual(Buyer, Now(l).OwnerID);
        l.Buyer.WaitForAlert(MoneyRules.Message(Refusal.PriceChanged));
    }

    /// <summary>money review 2026-11-15, R-10: group-owned land can't be sold for coins.</summary>
    [Fact]
    public void Group_owned_land_is_not_sold_for_coins()
    {
        var l = LandForSale(100);
        l.Parcel.LandData.GroupID = GroupId;
        l.Parcel.LandData.OwnerID = GroupId;
        l.Parcel.LandData.IsGroupOwned = true;

        Buy(l, 100);

        Assert.Empty(l.Region.Service.Calls);
        Assert.Equal(GroupId, Now(l).OwnerID);
        l.Buyer.WaitForAlert(MoneyRules.GroupLandText);
    }

    /// <summary>money review 2026-11-15, R-10: land can't be bought for a group with coins.</summary>
    [Fact]
    public void Land_is_not_bought_for_a_group_with_coins()
    {
        var l = LandForSale(100);

        Buy(l, 100, forGroup: true);

        Assert.Empty(l.Region.Service.Calls);
        Assert.Equal(Seller, Now(l).OwnerID);
        l.Buyer.WaitForAlert(MoneyRules.GroupLandText);
    }

    /// <summary>
    /// money review 2026-11-15, R-10: OnLandBuy completes the hold only when the land module made the buyer the owner;
    /// when it didn't, the coins go back.
    /// </summary>
    [Fact]
    public void The_hold_is_returned_when_the_land_module_does_not_make_the_buyer_the_owner()
    {
        var l = LandForSale(100);
        // Hooked last: after the money module's hold, the land module's own check now says no, so it moves nothing.
        l.Region.Scene.EventManager.OnValidateLandBuy += (_, e) => e.landValidated = false;

        Buy(l, 100);

        Assert.Single(l.Region.Service.Of("hold"));
        Assert.Equal(Seller, Now(l).OwnerID);
        Assert.Equal(7L, l.Region.WaitFor("return").Args[0]);
        Assert.Empty(l.Region.Service.Of("complete"));
    }

    /// <summary>money review 2026-11-15, R-10: free land changes hands without the money service.</summary>
    [Fact]
    public void Free_land_changes_hands_without_asking_the_money_service()
    {
        var l = LandForSale(0);

        Buy(l, 0);

        Assert.Equal(Buyer, Now(l).OwnerID);
        Assert.Empty(l.Region.Service.Calls);
    }

    // --- paying through the viewer's MoneyTransferRequest ------------------------------------------------------------

    /// <summary>
    /// money review 2026-11-15, R-10: the payer is the avatar on this client, with this client's session; the packet's
    /// source is only compared.
    /// </summary>
    [Fact]
    public void A_payment_is_made_as_the_clients_own_avatar_and_session()
    {
        var region = Build();
        var payer = region.Arrive(Buyer);
        region.Arrive(Seller);

        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Buyer, Seller, 5, Gift, "");

        var transfer = region.WaitFor("transfer");
        Assert.Equal("pay_avatar", transfer.Args[0]);
        var from = (MoneyWho)transfer.Args[1]!;
        Assert.Equal(Buyer.Guid, from.AgentId);
        Assert.Equal(payer.SessionId.Guid, from.SessionId);
        Assert.Equal(payer.SecureSessionId.Guid, from.SecureSessionId);
        Assert.Equal(Seller.Guid, ((MoneyWho)transfer.Args[2]!).AgentId);
        Assert.Equal(5L, transfer.Args[3]);
    }

    /// <summary>money review 2026-11-15, R-10: a packet naming someone else as the payer is refused, and nobody pays.</summary>
    [Fact]
    public void A_payment_with_a_forged_source_is_refused()
    {
        var region = Build();
        var payer = region.Arrive(Buyer);
        region.Arrive(Stranger);
        region.Arrive(Seller);

        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Stranger, Seller, 5, Gift, "");

        payer.WaitForAlert(MoneyRules.Message(Refusal.ForgedSource));
        // A real payment after it: had the forged one gone through, it would be there first.
        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Buyer, Seller, 6, Gift, "");
        region.WaitFor("transfer");
        var only = Assert.Single(region.Service.Of("transfer"));
        Assert.Equal(6L, only.Args[3]);
        Assert.Equal(Buyer.Guid, ((MoneyWho)only.Args[1]!).AgentId);
    }

    /// <summary>money review 2026-11-15, R-10: only gifts and object payments move money from a viewer's request.</summary>
    [Fact]
    public void Other_kinds_of_payment_from_the_viewer_are_refused()
    {
        var region = Build();
        var payer = region.Arrive(Buyer);

        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Buyer, Seller, 5, (int)MoneyTransactionType.LandSale, "");

        payer.WaitForAlert("That kind of payment isn't available on this grid.");
        Assert.Empty(region.Service.Calls);
    }

    /// <summary>money review 2026-11-15, R-10: paying an object pays its owner, and its script hears of it after.</summary>
    [Fact]
    public void Paying_an_object_pays_its_owner_and_tells_the_script()
    {
        var region = Build();
        var payer = region.Arrive(Buyer);
        var group = SceneHelpers.AddSceneObject(region.Scene, 2, Seller, "Tip jar ", 0x20);
        var child = group.Parts.First(p => !p.IsRoot);
        var paid = new List<(UUID Part, UUID Payer, int Amount)>();
        region.Module.OnObjectPaid += (part, who, amount) => { lock (paid) paid.Add((part, who, amount)); };

        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Buyer, child.UUID, 10, PayObject, "");

        var transfer = region.WaitFor("transfer");
        Assert.Equal("pay_object", transfer.Args[0]);
        Assert.Equal(Seller.Guid, ((MoneyWho)transfer.Args[2]!).AgentId);
        Assert.Equal(child.UUID.Guid, transfer.Args[5]);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            lock (paid) if (paid.Count > 0) break;
            Assert.True(DateTime.UtcNow < deadline, "OnObjectPaid wasn't raised.");
            Thread.Sleep(10);
        }
        Assert.Equal((child.UUID, Buyer, 10), Assert.Single(paid));
    }

    /// <summary>money review 2026-11-15, R-10: a group-owned object can't take payments.</summary>
    [Fact]
    public void Paying_a_group_owned_object_is_refused()
    {
        var region = Build();
        var payer = region.Arrive(Buyer);
        var group = SceneHelpers.AddSceneObject(region.Scene, 1, Seller, "Group jar ", 0x30);
        group.RootPart.GroupID = GroupId;
        group.RootPart.OwnerID = GroupId;

        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Buyer, group.RootPart.UUID, 10, PayObject, "");

        payer.WaitForAlert(MoneyRules.Message(Refusal.GroupOwned));
        Assert.Empty(region.Service.Calls);
    }

    // --- MoveMoney: the land module's parcel pass --------------------------------------------------------------------

    /// <summary>money review 2026-11-15, R-10: MoveMoney moves money for a parcel pass and nothing else.</summary>
    [Theory]
    [InlineData(MoneyTransactionType.Gift)]
    [InlineData(MoneyTransactionType.LandSale)]
    [InlineData(MoneyTransactionType.ObjectPays)]
    [InlineData(MoneyTransactionType.GroupJoin)]
    public void MoveMoney_moves_nothing_but_a_parcel_pass(MoneyTransactionType type)
    {
        var region = Build();
        region.Arrive(Buyer);

        Assert.False(region.Module.MoveMoney(Buyer, Seller, 5, type, "anything"));

        Assert.Empty(region.Service.Calls);
    }

    /// <summary>money review 2026-11-15, R-10: a parcel pass is a parcel_pass transfer from the client's avatar to the parcel owner.</summary>
    [Fact]
    public void MoveMoney_pays_a_parcel_pass()
    {
        var region = Build();
        var payer = region.Arrive(Buyer);

        Assert.True(region.Module.MoveMoney(Buyer, Seller, 5, MoneyTransactionType.LandPassSale, "pass"));

        var transfer = Assert.Single(region.Of("transfer", "parcel_pass"));
        Assert.Equal(payer.SessionId.Guid, ((MoneyWho)transfer.Args[1]!).SessionId);
        Assert.Equal(Seller.Guid, ((MoneyWho)transfer.Args[2]!).AgentId);
        Assert.Equal(5L, transfer.Args[3]);
    }

    private static (MoneyScene Region, LandManagementModule Lmm, ILandObject Parcel, RecordingClient Buyer) PassParcel()
    {
        var lmm = new LandManagementModule();
        var region = Build(others: [lmm]);
        region.Scene.loadAllLandObjectsFromStorage(region.Scene.RegionInfo.RegionID);
        var parcel = lmm.GetLandObject(128, 128);
        parcel.LandData.OwnerID = Seller;
        parcel.LandData.PassHours = 1;
        parcel.LandData.PassPrice = 10;
        parcel.LandData.Flags |= (uint)ParcelFlags.UsePassList;
        region.Service.Answer = _ => Rich;
        return (region, lmm, parcel, region.Arrive(Buyer));
    }

    /// <summary>money review 2026-11-15, R-10: buying a pass through the land module checks the balance, then pays the owner.</summary>
    [Fact]
    public void A_parcel_pass_bought_through_the_land_module_pays_the_parcel_owner()
    {
        var (region, lmm, parcel, buyer) = PassParcel();

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);

        var transfer = region.WaitFor("transfer");
        Assert.Equal("parcel_pass", transfer.Args[0]);
        Assert.Equal(Seller.Guid, ((MoneyWho)transfer.Args[2]!).AgentId);
        Assert.Equal(10L, transfer.Args[3]);
        lock (region.Service.Order) Assert.Equal(new[] { "balance", "transfer" }, region.Service.Order);
    }

    /// <summary>money review 2026-11-15, R-10: a pass to group-owned land is refused: a group holds no coins on this grid.</summary>
    [Fact]
    public void A_parcel_pass_on_group_owned_land_is_refused()
    {
        var (region, lmm, parcel, buyer) = PassParcel();
        parcel.LandData.GroupID = GroupId;
        parcel.LandData.OwnerID = GroupId;
        parcel.LandData.IsGroupOwned = true;

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);

        buyer.WaitForAlert("group owned");
        Thread.Sleep(200); // the pass's payment would run on OpenSim's thread pool
        Assert.Empty(region.Service.Calls);
        Assert.DoesNotContain(parcel.LandData.ParcelAccessList, e => e.AgentID == Buyer);
    }

    // --- ObjectGiveMoney: script payouts -----------------------------------------------------------------------------

    /// <summary>money review 2026-11-15, R-10: a script payout from a group-owned object is refused before the service.</summary>
    [Fact]
    public void A_payout_from_a_group_owned_object_is_refused()
    {
        var region = Build();
        var group = SceneHelpers.AddSceneObject(region.Scene, 1, Seller, "Vendor ", 0x40);
        group.RootPart.GroupID = GroupId;
        group.RootPart.OwnerID = GroupId;

        Assert.False(region.Module.ObjectGiveMoney(group.RootPart.UUID, GroupId, Buyer, 5, UUID.Zero, out var reason));

        Assert.Equal("GROUP_OWNED", reason);
        Assert.Empty(region.Service.Calls);
    }

    /// <summary>money review 2026-11-15, R-10: a payout naming anyone but the object's owner as the payer is refused.</summary>
    [Fact]
    public void A_payout_from_someone_other_than_the_owner_is_refused()
    {
        var region = Build();
        var group = SceneHelpers.AddSceneObject(region.Scene, 1, Seller, "Vendor ", 0x50);

        Assert.False(region.Module.ObjectGiveMoney(group.RootPart.UUID, Stranger, Buyer, 5, UUID.Zero, out var reason));

        Assert.Equal("SERVICE_ERROR", reason);
        Assert.Empty(region.Service.Calls);
    }

    /// <summary>money review 2026-11-15, R-10: a payout from a child prim is paid by the object's owner, named by its root.</summary>
    [Fact]
    public void A_payout_from_a_child_prim_is_paid_as_the_whole_object()
    {
        var region = Build();
        var group = SceneHelpers.AddSceneObject(region.Scene, 2, Seller, "Vendor ", 0x60);
        var child = group.Parts.First(p => !p.IsRoot);
        var txn = TestHelpers.ParseTail(0x61);

        Assert.True(region.Module.ObjectGiveMoney(child.UUID, Seller, Buyer, 5, txn, out var reason));

        Assert.Equal("", reason);
        var payout = Assert.Single(region.Service.Of("payout"));
        Assert.Equal(txn.Guid, payout.Txn);
        Assert.Equal(Seller.Guid, payout.Args[0]);
        Assert.Equal(Buyer.Guid, ((MoneyWho)payout.Args[1]!).AgentId);
        Assert.Equal(group.RootPart.UUID.Guid, payout.Args[4]);
        Assert.Equal(group.RootPart.Name, payout.Args[5]);
    }

    /// <summary>money review 2026-11-15, R-10: a payout from an object that isn't in any region is refused.</summary>
    [Fact]
    public void A_payout_from_an_object_not_here_is_refused()
    {
        var region = Build();

        Assert.False(region.Module.ObjectGiveMoney(TestHelpers.ParseTail(0x70), Seller, Buyer, 5, UUID.Zero, out var reason));

        Assert.Equal("SERVICE_ERROR", reason);
        Assert.Empty(region.Service.Calls);
    }

    // --- ApplyCharge: where each fee goes ----------------------------------------------------------------------------

    /// <summary>money review 2026-11-15, R-10: a group join fee is paid to the group's founder.</summary>
    [Fact]
    public void A_group_join_fee_is_paid_to_the_founder()
    {
        var region = Build();
        region.Arrive(Buyer);
        // After the arrival: the scene asks a groups module for titles as an avatar arrives, which this one doesn't answer.
        region.Scene.RegisterModuleInterface(OneGroup.Create("Club", Founder));

        region.Module.ApplyCharge(Buyer, 20, MoneyTransactionType.GroupJoin, "Club");

        var charge = Assert.Single(region.Service.Of("charge"));
        Assert.Equal("group_join_fee", charge.Args[0]);
        Assert.Equal(Buyer.Guid, ((MoneyWho)charge.Args[1]!).AgentId);
        Assert.Equal(20L, charge.Args[2]);
        Assert.Equal(Founder.Guid, charge.Args[3]);
    }

    /// <summary>money review 2026-11-15, R-10: a group join fee for a group the region can't look up goes to the grid.</summary>
    [Fact]
    public void A_group_join_fee_without_a_known_founder_goes_to_the_grid()
    {
        var region = Build();
        region.Arrive(Buyer);
        region.Scene.RegisterModuleInterface(OneGroup.Create("Club", Founder));

        region.Module.ApplyCharge(Buyer, 20, MoneyTransactionType.GroupJoin, "Another club");

        var charge = Assert.Single(region.Service.Of("charge"));
        Assert.Equal("group_join_fee", charge.Args[0]);
        Assert.Null(charge.Args[3]);
    }

    /// <summary>money review 2026-11-15, R-10: a classified is charged the grid's fee, whatever price the viewer sent.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    public void A_classified_charge_is_the_grids_fee_not_the_viewers_price(int viewerPrice)
    {
        var region = Build(new EconomyInfo("C$", 0, 25, true, true, 1_000_000));
        region.Arrive(Buyer);

        region.Module.ApplyCharge(Buyer, viewerPrice, MoneyTransactionType.ClassifiedCharge);

        var charge = Assert.Single(region.Service.Of("charge"));
        Assert.Equal("classified_fee", charge.Args[0]);
        Assert.Equal(25L, charge.Args[2]);
        Assert.Null(charge.Args[3]);
    }

    /// <summary>money review 2026-11-15, R-10: other charge types OpenSim might send take nothing.</summary>
    [Theory]
    [InlineData(MoneyTransactionType.LandSale)]
    [InlineData(MoneyTransactionType.Gift)]
    [InlineData(MoneyTransactionType.ObjectSale)]
    public void Other_charges_take_nothing(MoneyTransactionType type)
    {
        var region = Build();
        region.Arrive(Buyer);

        region.Module.ApplyCharge(Buyer, 20, type);

        Assert.Empty(region.Service.Calls);
    }

    // --- Info(): whether a contents sale has anything to sell --------------------------------------------------------

    private static (MoneyScene Region, RecordingClient Buyer, SceneObjectGroup Box) ContentsForSale(params bool[] transferable)
    {
        var region = Build();
        region.Service.Answer = _ => Rich;
        var box = SceneHelpers.AddSceneObject(region.Scene, 1, Seller, "Box ", 0x80);
        box.RootPart.ObjectSaleType = MoneyRules.SaleContents;
        box.RootPart.SalePrice = 50;
        var i = 0;
        foreach (var t in transferable)
        {
            var item = TaskInventoryHelpers.AddScript(region.Scene.AssetService, box.RootPart, TestHelpers.ParseTail(0x90 + i),
                TestHelpers.ParseTail(0xa0 + i), "item " + i, "default { state_entry() { } }");
            i++;
            item.CurrentPermissions = t ? (uint)OpenSim.Framework.PermissionMask.All : (uint)(OpenSim.Framework.PermissionMask.All & ~OpenSim.Framework.PermissionMask.Transfer);
        }
        return (region, region.Arrive(Buyer), box);
    }

    private static void BuyBox(RecordingClient buyer, SceneObjectGroup box) =>
        buyer.Raise(nameof(IClientAPI.OnObjectBuy), buyer, Buyer, buyer.SessionId, UUID.Zero, UUID.Zero, box.RootPart.LocalId,
            MoneyRules.SaleContents, 50);

    /// <summary>money review 2026-11-15, R-10: a contents sale whose contents can all be given goes ahead to the hold.</summary>
    [Fact]
    public void A_contents_sale_with_only_transferable_contents_is_held()
    {
        var (region, buyer, box) = ContentsForSale(true, true);

        BuyBox(buyer, box);

        var hold = region.WaitFor("hold");
        Assert.Equal("object", hold.Args[0]);
        Assert.Equal(50L, hold.Args[3]);
    }

    /// <summary>money review 2026-11-15, R-10: a contents sale with anything the buyer couldn't be given is refused.</summary>
    [Fact]
    public void A_contents_sale_with_a_no_transfer_item_is_refused()
    {
        var (region, buyer, box) = ContentsForSale(true, false);

        BuyBox(buyer, box);

        buyer.WaitForAlert(MoneyRules.Message(Refusal.NothingToSell));
        Assert.Empty(region.Service.Calls);
    }

    /// <summary>money review 2026-11-15, R-10: a contents sale of an empty object is refused.</summary>
    [Fact]
    public void A_contents_sale_of_an_empty_object_is_refused()
    {
        var (region, buyer, box) = ContentsForSale();

        BuyBox(buyer, box);

        buyer.WaitForAlert(MoneyRules.Message(Refusal.NothingToSell));
        Assert.Empty(region.Service.Calls);
    }
}
