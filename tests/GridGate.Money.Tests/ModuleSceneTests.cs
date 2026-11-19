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
    private static void Buy(Land l, int price, bool forGroup = false, UUID? who = null) =>
        l.Region.Scene.ProcessParcelBuy(who ?? Buyer, forGroup ? GroupId : UUID.Zero, true, forGroup, false, l.Parcel.LandData.LocalID,
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

    /// <summary>Runs <paramref name="change"/> on the parcel while the money service is being asked to hold, then buys.</summary>
    private static Land BuyWhileHolding(int price, Action<Land> change)
    {
        var l = LandForSale(price);
        l.Region.Service.Gate = m =>
        {
            if (m == "hold") change(l);
            return Task.CompletedTask;
        };
        Buy(l, price);
        return l;
    }

    private static void AssertLandRefusedAndHoldReturned(Land l)
    {
        Assert.Equal(7L, l.Region.WaitFor("return").Args[0]);
        Assert.Empty(l.Region.Service.Of("complete"));
        Assert.Equal(Seller, Now(l).OwnerID);
        l.Buyer.WaitForAlert(MoneyRules.Message(Refusal.PriceChanged));
    }

    [Fact]
    public void Land_subdivided_during_the_hold_is_not_sold_and_the_hold_is_returned()
    {
        var l = BuyWhileHolding(100, x => x.Lmm.Subdivide(0, 0, 64, 64, Seller));

        AssertLandRefusedAndHoldReturned(l);
    }

    [Fact]
    public void Land_taken_off_sale_during_the_hold_is_not_sold_and_the_hold_is_returned()
    {
        var l = BuyWhileHolding(100, x => x.Parcel.LandData.Flags &= ~(uint)ParcelFlags.ForSale);

        AssertLandRefusedAndHoldReturned(l);
    }

    [Fact]
    public void Land_repriced_during_the_hold_is_not_sold_and_the_hold_is_returned()
    {
        var l = BuyWhileHolding(100, x => x.Parcel.LandData.SalePrice = 500);

        AssertLandRefusedAndHoldReturned(l);
    }

    [Fact]
    public void Land_sold_to_someone_else_only_during_the_hold_is_not_sold()
    {
        var l = BuyWhileHolding(100, x => x.Parcel.LandData.AuthBuyerID = Stranger);

        AssertLandRefusedAndHoldReturned(l);
    }

    [Fact]
    public void Land_whose_area_changed_before_the_buy_is_refused_before_any_hold()
    {
        var l = LandForSale(100);
        l.Lmm.Subdivide(0, 0, 64, 64, Seller);
        var wrongArea = l.Parcel.LandData.Area + 4096;

        l.Region.Scene.ProcessParcelBuy(Buyer, UUID.Zero, true, false, false, l.Parcel.LandData.LocalID, wrongArea, 100, false);

        Assert.Empty(l.Region.Service.Calls);
        Assert.Equal(Seller, Now(l).OwnerID);
        l.Buyer.WaitForAlert(MoneyRules.Message(Refusal.PriceChanged));
    }

    [Fact]
    public void A_sixth_land_buy_in_a_minute_is_refused_without_asking_the_service()
    {
        var l = LandForSale(100);
        l.Region.Service.Queue("hold", Enumerable.Repeat(new MoneyAnswer("not_enough"), 5).ToArray());

        for (var i = 0; i < 6; i++) Buy(l, 100);

        Assert.Equal(5, l.Region.Service.Of("hold").Count());
        l.Buyer.WaitForAlert(MoneyRules.LandTooFastText);
    }

    [Fact]
    public async Task Two_buyers_of_one_parcel_at_once_hold_only_once()
    {
        var l = LandForSale(100);
        var stranger = l.Region.Arrive(Stranger);
        var release = new TaskCompletionSource();
        l.Region.Service.Gate = m => m == "hold" ? release.Task : Task.CompletedTask;

        var first = Task.Run(() => Buy(l, 100), TestContext.Current.CancellationToken);
        l.Region.WaitFor("hold");
        Buy(l, 100, who: Stranger);

        Assert.Single(l.Region.Service.Of("hold"));
        stranger.WaitForAlert("Someone is buying this parcel right now. Please try again in a moment.");
        release.SetResult();
        await first;
    }

    [Fact]
    public void A_purchase_whose_parcel_mark_was_taken_over_meanwhile_returns_its_hold_and_moves_no_land()
    {
        var l = LandForSale(100);
        // While the service is asked, another purchase takes the parcel's mark over (as one does when the mark looks a
        // minute old): the module's entry for the parcel is no longer this purchase's.
        l.Region.Service.Gate = m =>
        {
            if (m == "hold")
            {
                var marks = (System.Collections.IDictionary)typeof(GridGateMoneyModule)
                    .GetField("_landHolds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(l.Region.Module)!;
                var key = marks.Keys.Cast<object>().Single();
                var mine = marks[key]!;
                var theirs = mine.GetType().GetMethod("<Clone>$")!.Invoke(mine, null)!;
                mine.GetType().GetProperty("Agent")!.SetValue(theirs, Stranger);
                marks[key] = theirs;
            }
            return Task.CompletedTask;
        };

        Buy(l, 100);

        Assert.Equal(7L, l.Region.WaitFor("return").Args[0]);
        Assert.Empty(l.Region.Service.Of("complete"));
        Assert.Equal(Seller, Now(l).OwnerID);
        l.Buyer.WaitForAlert("Someone is buying this parcel right now. Please try again in a moment.");
    }

    [Fact]
    public void A_land_hold_waits_at_most_one_and_a_half_seconds()
    {
        var l = LandForSale(100);
        l.Region.Service.Gate = _ => new TaskCompletionSource().Task; // the service never answers

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Buy(l, 100);
        watch.Stop();

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.4), TimeSpan.FromSeconds(2.5));
        Assert.Equal(Seller, Now(l).OwnerID);
    }

    [Fact]
    public void A_land_hold_the_service_places_after_the_wait_is_returned_at_once()
    {
        var l = LandForSale(100);
        var release = new TaskCompletionSource();
        l.Region.Service.Gate = m => m == "hold" ? release.Task : Task.CompletedTask;

        Buy(l, 100); // gives up after one and a half seconds
        Assert.Equal(Seller, Now(l).OwnerID);
        Assert.Empty(l.Region.Service.Of("return"));
        release.SetResult(); // the service answers after all, with a hold

        Assert.Equal(7L, l.Region.WaitFor("return").Args[0]);
        Assert.Empty(l.Region.Service.Of("complete"));
        Assert.Equal(Seller, Now(l).OwnerID);
    }

    [Fact]
    public void After_a_land_hold_runs_out_of_time_land_buys_in_the_region_fail_fast()
    {
        var l = LandForSale(100);
        var stranger = l.Region.Arrive(Stranger);
        l.Region.Service.Gate = _ => new TaskCompletionSource().Task;
        Buy(l, 100);
        Assert.Single(l.Region.Service.Of("hold"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Buy(l, 100, who: Stranger);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"The second buy waited {watch.Elapsed}.");
        Assert.Single(l.Region.Service.Of("hold"));
        stranger.WaitForAlert(MoneyRules.Message("unavailable"));
        Assert.Equal(Seller, Now(l).OwnerID);
    }

    // --- visitors who are not here ------------------------------------------------------------------------------------

    private const string TheirHome = "http://theirs.example:8002/";

    private static MoneyScene VisitorRegion() =>
        Build(beforeMoney: scene => scene.RegisterModuleInterface(CachedVisitors.Create(Stranger)));

    [Fact]
    public void An_absent_visitor_known_only_by_the_name_cache_is_not_paid_and_the_payer_is_told()
    {
        var region = VisitorRegion();
        var payer = region.Arrive(Buyer);

        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Buyer, Stranger, 5, Gift, "");

        payer.WaitForAlert(MoneyRules.AbsentVisitorText);
        Assert.Empty(region.Service.Of("transfer"));
    }

    [Fact]
    public void A_visitor_who_is_here_is_paid_by_the_home_of_their_login()
    {
        var region = VisitorRegion();
        var payer = region.Arrive(Buyer);
        region.Arrive(Stranger, TheirHome);

        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Buyer, Stranger, 5, Gift, "");

        var transfer = region.WaitFor("transfer");
        Assert.Equal(TheirHome, ((MoneyWho)transfer.Args[2]!).Home);
    }

    [Fact]
    public void A_visitor_remembered_from_an_earlier_arrival_is_still_paid_after_they_leave()
    {
        var region = VisitorRegion();
        var payer = region.Arrive(Buyer);
        region.Arrive(Stranger, TheirHome);
        region.Scene.CloseAgent(Stranger, false);
        Assert.False(region.Scene.TryGetScenePresence(Stranger, out ScenePresence _));

        payer.Raise(nameof(IClientAPI.OnMoneyTransferRequest), Buyer, Stranger, 5, Gift, "");

        Assert.Equal(TheirHome, ((MoneyWho)region.WaitFor("transfer").Args[2]!).Home);
    }

    [Fact]
    public void A_parcel_pass_to_an_absent_visitor_is_not_paid_and_the_payer_is_told()
    {
        var region = VisitorRegion();
        var payer = region.Arrive(Buyer);

        Assert.False(region.Module.MoveMoney(Buyer, Stranger, 5, MoneyTransactionType.LandPassSale, "pass"));

        Assert.Contains(MoneyRules.AbsentVisitorText, payer.Alerts);
        Assert.Empty(region.Service.Of("transfer"));
    }

    [Fact]
    public void An_absent_visitor_known_only_by_the_name_cache_cannot_be_paid_by_a_script()
    {
        var region = VisitorRegion();
        region.Arrive(Buyer);
        var box = SceneHelpers.AddSceneObject(region.Scene, 1, Buyer, "Box ", 0x80);

        Assert.False(region.Module.ObjectGiveMoney(box.RootPart.UUID, Buyer, Stranger, 5, UUID.Random(), out var reason));

        Assert.Equal("LINDENDOLLAR_ENTITYDOESNOTEXIST", reason);
        Assert.Empty(region.Service.Of("payout"));
    }

    [Fact]
    public void An_absent_visitor_known_only_by_the_name_cache_cannot_be_paid_through_CanPayVisitor()
    {
        var region = VisitorRegion();
        Assert.False(region.Module.CanPayVisitor(Stranger));

        region.Arrive(Stranger, TheirHome);
        Assert.True(region.Module.CanPayVisitor(Stranger));
    }

    [Fact]
    public void Buying_two_parcels_one_after_the_other_works()
    {
        var l = LandForSale(100);
        l.Lmm.Subdivide(0, 0, 128, 256, Seller);
        var first = l.Lmm.GetLandObject(10, 10);
        var second = l.Lmm.GetLandObject(200, 10);
        Assert.NotEqual(first.LandData.LocalID, second.LandData.LocalID);

        Buy(l with { Parcel = first }, 100);
        Buy(l with { Parcel = second }, 100);

        Assert.Equal(2, l.Region.Service.Of("hold").Count());
        l.Region.WaitFor("complete", c => (long)c.Args[0]! == 7L);
        Assert.Equal(Buyer, l.Lmm.GetLandObject(10, 10).LandData.OwnerID);
        Assert.Equal(Buyer, l.Lmm.GetLandObject(200, 10).LandData.OwnerID);
    }

    [Fact]
    public void A_buyer_refused_for_coins_can_try_again_at_once()
    {
        var l = LandForSale(100);
        l.Region.Service.Queue("hold", new MoneyAnswer("not_enough"), Rich with { HoldId = 7 });

        Buy(l, 100);
        Assert.Equal(Seller, Now(l).OwnerID);
        Buy(l, 100);

        Assert.Equal(2, l.Region.Service.Of("hold").Count());
        Assert.Equal(Buyer, Now(l).OwnerID);
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

    private static (MoneyScene Region, LandManagementModule Lmm, ILandObject Parcel, RecordingClient Buyer) PassParcel(bool priceShown = true)
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
        var buyer = region.Arrive(Buyer);
        // The viewer has the parcel's properties, with the pass price, before it offers the pass.
        if (priceShown) parcel.SendLandProperties(0, false, 0, buyer);
        return (region, lmm, parcel, buyer);
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

    [Fact]
    public void A_pass_with_no_price_shown_is_refused_with_the_current_price()
    {
        var (region, lmm, parcel, _) = PassParcel();
        // A viewer the region has sent no parcel properties to (the buyer in the region was sent them on arrival).
        var data = SceneHelpers.GenerateAgentData(Stranger);
        var unshown = new RecordingClient(data, region.Scene);

        lmm.ClientParcelBuyPass(unshown, Stranger, parcel.LandData.LocalID);

        unshown.WaitForAlert("10");
        Thread.Sleep(200);
        Assert.Empty(region.Service.Calls);
        Assert.DoesNotContain(parcel.LandData.ParcelAccessList, e => e.AgentID == Stranger);
    }

    [Fact]
    public void A_pass_bought_after_the_price_went_up_is_refused_with_the_new_price_and_the_next_click_pays_it()
    {
        var (region, lmm, parcel, buyer) = PassParcel();
        parcel.LandData.PassPrice = 500;

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);

        buyer.WaitForAlert("500");
        Thread.Sleep(200);
        Assert.Empty(region.Service.Calls);

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);

        Assert.Equal(500L, region.WaitFor("transfer").Args[3]);
    }

    [Fact]
    public void A_pass_bought_after_the_price_went_down_costs_the_lower_price()
    {
        var (region, lmm, parcel, buyer) = PassParcel();
        parcel.LandData.PassPrice = 4;

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);

        Assert.Equal(4L, region.WaitFor("transfer").Args[3]);
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

    private const string TryAgainLater = "Sorry pass payment processing failed, please try again later";

    [Fact]
    public void A_second_pass_bought_while_the_first_is_in_flight_is_refused_and_not_paid()
    {
        var (region, lmm, parcel, buyer) = PassParcel();
        var release = new TaskCompletionSource();
        region.Service.Gate = m => m == "transfer" ? release.Task : Task.CompletedTask;

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);
        region.WaitFor("transfer");
        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);

        buyer.WaitForAlert("Your pass for this parcel is still being bought.");
        release.SetResult();
        WaitUntil(() => parcel.LandData.ParcelAccessList.Any(e => e.AgentID == Buyer));
        Thread.Sleep(200);
        Assert.Single(region.Service.Of("transfer"));
    }

    [Fact]
    public void A_pass_renewal_replaces_only_the_buyers_own_entry()
    {
        var (region, lmm, parcel, buyer) = PassParcel();
        var other = UUID.Random();
        var early = UUID.Random();
        var list = parcel.LandData.ParcelAccessList;
        list.Add(new LandAccessEntry { AgentID = other, Flags = AccessList.Access, Expires = 0 });
        list.Add(new LandAccessEntry { AgentID = Buyer, Flags = AccessList.Access, Expires = Util.UnixTimeSinceEpoch() + 100 });
        var release = new TaskCompletionSource();
        region.Service.Gate = m => m == "transfer" ? release.Task : Task.CompletedTask;

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);
        region.WaitFor("transfer");
        lock (list) list.Insert(0, new LandAccessEntry { AgentID = early, Flags = AccessList.Access, Expires = 0 });
        release.SetResult();

        WaitUntil(() => list.Any(e => e.AgentID == Buyer && e.Expires > Util.UnixTimeSinceEpoch() + 3000));
        lock (list)
        {
            Assert.Single(list, e => e.AgentID == Buyer);
            Assert.Single(list, e => e.AgentID == other);
            Assert.Single(list, e => e.AgentID == early);
            Assert.Equal(3, list.Count);
        }
    }

    [Fact]
    public void A_pass_whose_payment_is_being_checked_does_not_tell_the_buyer_to_try_again()
    {
        var (region, lmm, parcel, buyer) = PassParcel();
        region.Service.Answer = m => m == "transfer" ? MoneyAnswer.NoAnswer : Rich;

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);

        buyer.WaitForAlert(MoneyRules.CheckingText);
        Thread.Sleep(300);
        Assert.DoesNotContain(buyer.Alerts, a => a.Contains("try again later", StringComparison.Ordinal));
        Assert.DoesNotContain(parcel.LandData.ParcelAccessList, e => e.AgentID == Buyer);
    }

    [Fact]
    public void A_pass_refused_for_too_few_coins_is_explained_once()
    {
        var (region, lmm, parcel, buyer) = PassParcel();
        region.Service.Answer = m => m == "balance" ? new MoneyAnswer("ok", 5, 0, 1, 7) : Rich;

        lmm.ClientParcelBuyPass(buyer, Buyer, parcel.LandData.LocalID);

        buyer.WaitForAlert("Insufficient funds");
        Thread.Sleep(300);
        Assert.Single(buyer.Alerts);
        Assert.Empty(region.Service.Of("transfer"));
    }

    private static void WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            Thread.Sleep(10);
        }
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

    [Fact]
    public void An_owner_past_sixty_payouts_a_minute_is_refused()
    {
        var region = Build();
        var obj = SceneHelpers.AddSceneObject(region.Scene, 1, Seller, "Vendor ", 0x80);
        for (var i = 0; i < PayoutLimit.PerWindow; i++)
            Assert.True(region.Module.ObjectGiveMoney(obj.RootPart.UUID, Seller, Buyer, 1, UUID.Zero, out _));

        Assert.False(region.Module.ObjectGiveMoney(obj.RootPart.UUID, Seller, Buyer, 1, UUID.Zero, out var reason));

        Assert.Equal("SERVICE_ERROR", reason);
        Assert.Equal(PayoutLimit.PerWindow, region.Service.Of("payout").Count());
    }

    [Fact]
    public void Payouts_by_different_owners_have_their_own_limits()
    {
        var region = Build();
        var mine = SceneHelpers.AddSceneObject(region.Scene, 1, Seller, "Vendor ", 0x81);
        var theirs = SceneHelpers.AddSceneObject(region.Scene, 1, Stranger, "Vendor ", 0x82);
        for (var i = 0; i < PayoutLimit.PerWindow; i++)
            region.Module.ObjectGiveMoney(mine.RootPart.UUID, Seller, Buyer, 1, UUID.Zero, out _);

        Assert.True(region.Module.ObjectGiveMoney(theirs.RootPart.UUID, Stranger, Buyer, 1, UUID.Zero, out _));
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

        region.Module.ApplyCharge(Buyer, 20, MoneyTransactionType.GroupJoin, GroupId.ToString());

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

        region.Module.ApplyCharge(Buyer, 20, MoneyTransactionType.GroupJoin, Stranger.ToString());

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

    /// <summary>A fee is charged in the region the payer is in, so the service can check they are there.</summary>
    [Fact]
    public void Charge_sends_the_region()
    {
        var region = Build();
        region.Arrive(Buyer);

        region.Module.ApplyUploadCharge(Buyer, 3, "upload");

        var charge = Assert.Single(region.Service.Of("charge"));
        Assert.Equal("upload_fee", charge.Args[0]);
        Assert.Equal(region.Scene.RegionInfo.RegionID.Guid, charge.Args[4]);
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

    // --- HoldFee: a fee held before its action -----------------------------------------------------------------------

    [Fact]
    public void HoldFee_for_a_classified_holds_the_grids_fee_not_the_viewers_price()
    {
        var region = Build(new EconomyInfo("C$", 0, 25, true, true, 1_000_000, FeeHolds: true));
        region.Arrive(Buyer);

        var fee = region.Module.HoldFee(Buyer, 0, MoneyTransactionType.ClassifiedCharge);

        Assert.NotNull(fee);
        var hold = Assert.Single(region.Service.Of("hold"));
        Assert.Equal("classified_fee", hold.Args[0]);
        Assert.Null(hold.Args[2]);
        Assert.Equal(25L, hold.Args[3]);
        Assert.Equal(region.Scene.RegionInfo.RegionID.Guid, hold.Args[4]);
        fee.Settle(true);
        Assert.Equal(7L, region.WaitFor("complete").Args[0]);
        Assert.Empty(region.Service.Of("charge"));
    }

    [Fact]
    public void HoldFee_for_a_group_join_pays_the_founder()
    {
        var region = Build(new EconomyInfo("C$", 0, 0, true, true, 1_000_000, FeeHolds: true));
        region.Arrive(Buyer);
        region.Scene.RegisterModuleInterface(OneGroup.Create("Club", Founder));

        var fee = region.Module.HoldFee(Buyer, 20, MoneyTransactionType.GroupJoin, GroupId.ToString());

        var hold = Assert.Single(region.Service.Of("hold"));
        Assert.Equal("group_join_fee", hold.Args[0]);
        Assert.Equal(Founder.Guid, ((MoneyWho)hold.Args[2]!).AgentId);
        Assert.Equal(20L, hold.Args[3]);
        fee!.Settle(false);
        region.WaitFor("return");
        Assert.Empty(region.Service.Of("complete"));
    }

    [Fact]
    public void A_fee_settles_once()
    {
        var region = Build(new EconomyInfo("C$", 10, 0, true, true, 1_000_000, FeeHolds: true));
        region.Arrive(Buyer);

        var fee = region.Module.HoldFee(Buyer, 10, MoneyTransactionType.UploadCharge)!;
        fee.Settle(true);
        fee.Settle(false);
        fee.Settle(true);

        region.WaitFor("complete");
        Thread.Sleep(200);
        Assert.Single(region.Service.Of("complete"));
        Assert.Empty(region.Service.Of("return"));
    }

    [Fact]
    public void Without_fee_holds_on_the_service_every_fee_answers_unavailable()
    {
        // A service from before fee holds: no flag in its economy answer.
        var region = Build(new EconomyInfo("C$", 10, 25, true, true, 1_000_000));
        var client = region.Arrive(Buyer);
        region.Service.Answer = _ => Rich;
        region.Scene.RegisterModuleInterface(OneGroup.Create("Club", Founder));

        Assert.Null(region.Module.HoldFee(Buyer, 10, MoneyTransactionType.UploadCharge));
        Assert.Null(region.Module.HoldFee(Buyer, 25, MoneyTransactionType.ClassifiedCharge));
        Assert.Null(region.Module.HoldFee(Buyer, 20, MoneyTransactionType.GroupJoin, MoneyScene.GroupId.ToString()));

        Assert.Equal(3, client.Alerts.Count(a => a == MoneyRules.Message("unavailable")));
        Assert.Empty(region.Service.Calls);
    }

    [Fact]
    public void A_fee_of_nothing_is_still_due_nothing_without_fee_holds()
    {
        var region = Build(new EconomyInfo("C$", 10, 0, true, true, 1_000_000));
        region.Arrive(Buyer);

        Assert.NotNull(region.Module.HoldFee(Buyer, 0, MoneyTransactionType.UploadCharge));
        Assert.Empty(region.Service.Calls);
    }

    [Fact]
    public void A_fee_for_someone_not_here_is_refused()
    {
        var region = Build(new EconomyInfo("C$", 10, 0, true, true, 1_000_000, FeeHolds: true));

        Assert.Null(region.Module.HoldFee(Stranger, 10, MoneyTransactionType.UploadCharge));
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
