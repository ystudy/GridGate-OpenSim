using System.Reflection;
using GridGate.Region.Money;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>Every money action the region module takes (money design §7.3), against a fake service (money plan 2, task 4).</summary>
public sealed class MoneyActionsTests
{
    private static readonly Guid Me = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Them = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Group = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly Guid PartId = Guid.Parse("d0000000-0000-0000-0000-00000000000d");
    private static readonly Payer P = new(Me, "", Guid.Parse("50000000-0000-0000-0000-000000000005"), Guid.Parse("60000000-0000-0000-0000-000000000006"));
    private static readonly MoneyWho ThemWho = new(Them, "");

    private readonly FakeMoneyService _service = new();
    private readonly FakeView _view = new();
    private EconomyInfo _economy = new("C$", 0, 0, true, true, 1_000_000);
    private readonly MoneyActions _actions;

    public MoneyActionsTests() => _actions = new MoneyActions(_service, Region, () => _economy, _view);

    private static PartInfo Part(int[]? payPrice = null, Guid? owner = null, Guid? group = null, byte saleType = 1, int salePrice = 10) =>
        new(PartId, PartId, true, owner ?? Them, group ?? Guid.Empty, "dress", payPrice ?? [-2, -2, -2, -2, -2], saleType, salePrice, true);

    private static int Type(string name) => (int)Enum.Parse(MoneyActions.TransactionTypes, name);

    [Fact]
    public void Transaction_type_names_exist_in_libomv()
    {
        foreach (var name in new[] { "Gift", "PayObject", "ObjectSale", "LandSale", "LandPassSale", "ObjectPays", "UploadCharge", "GroupJoin", "ClassifiedCharge" })
            Assert.True(Enum.IsDefined(MoneyActions.TransactionTypes, Enum.Parse(MoneyActions.TransactionTypes, name)), name);
        Assert.Equal("OpenMetaverse.MoneyTransactionType", MoneyActions.TransactionTypes.FullName);
    }

    [Fact]
    public async Task Pay_avatar_moves_then_pushes_both()
    {
        _service.Queue("transfer", new MoneyAnswer("ok", 70, 130, 1));

        await _actions.PayAvatarAsync(P, Me, ThemWho, 30);

        var call = Assert.Single(_service.Of("transfer"));
        Assert.Equal("pay_avatar", call.Args[0]);
        Assert.Equal(new MoneyWho(Me, "", P.SessionId, P.SecureSessionId), call.Args[1]);
        Assert.Equal(30L, call.Args[3]);
        Assert.Equal(Region, call.Args[4]);
        var note = new BalanceNote(Type("Gift"), Me, Them, 30, "");
        Assert.Equal([(Me, 70L, (BalanceNote?)note), (Them, 130L, (BalanceNote?)note)], _view.Pushed);
    }

    [Fact]
    public async Task Forged_pay_makes_no_call()
    {
        await _actions.PayAvatarAsync(P, Them, ThemWho, 30);

        Assert.Empty(_service.Calls);
        Assert.Single(_view.Told);
    }

    [Fact]
    public async Task Pay_object_raises_paid_after_ok()
    {
        _service.Queue("transfer", new MoneyAnswer("ok", 95, 105, 1));
        var part = Part();

        await _actions.PayObjectAsync(P, Me, part, part, ThemWho, 5);

        var call = Assert.Single(_service.Of("transfer"));
        Assert.Equal("pay_object", call.Args[0]);
        Assert.Equal(PartId, call.Args[5]);
        Assert.Equal("dress", call.Args[6]);
        Assert.Equal((PartId, Me, 5), Assert.Single(_view.Paid));
        Assert.Equal("paid", _view.Order[0]);
        Assert.Contains((Me, 95L, (BalanceNote?)new BalanceNote(Type("PayObject"), Me, Them, 5, "dress")), _view.Pushed);
    }

    [Fact]
    public async Task Pay_object_refused_raises_nothing()
    {
        _service.Queue("transfer", new MoneyAnswer("not_enough"));
        var part = Part();

        await _actions.PayObjectAsync(P, Me, part, part, ThemWho, 5);

        Assert.Empty(_view.Paid);
        Assert.Empty(_view.Pushed);
        Assert.Equal((Me, "You don't have enough coins for that."), Assert.Single(_view.Told));
    }

    [Fact]
    public async Task Pay_object_wrong_amount_makes_no_call()
    {
        var part = Part(payPrice: [-1, 10, 25, -1, -1]);

        await _actions.PayObjectAsync(P, Me, part, part, ThemWho, 11);

        Assert.Empty(_service.Calls);
        Assert.Equal((Me, "That amount isn't one this object accepts."), Assert.Single(_view.Told));
    }

    [Fact]
    public async Task Group_owned_object_payment_makes_no_call()
    {
        var part = Part(owner: Group, group: Group);

        await _actions.PayObjectAsync(P, Me, part, part, new MoneyWho(Group, ""), 5);

        Assert.Empty(_service.Calls);
        Assert.Empty(_view.Paid);
        Assert.Equal(MoneyRules.Message(Refusal.GroupOwned), Assert.Single(_view.Told).Text);
    }

    [Fact]
    public async Task Buy_delivers_after_hold_and_completes()
    {
        _service.Queue("hold", new MoneyAnswer("ok", 90, null, 1, 7));
        _service.Queue("complete", new MoneyAnswer("ok", null, 110, 2, 7));
        var delivered = false;

        var bought = await _actions.BuyAsync(P, Part(), ThemWho, 1, 10, () => { _service.Order.Add("deliver"); return delivered = true; });

        Assert.True(bought);
        Assert.True(delivered);
        Assert.Equal(["hold", "deliver", "complete"], _service.Order);
        var hold = Assert.Single(_service.Of("hold"));
        Assert.Equal("object", hold.Args[0]);
        Assert.Equal(10L, hold.Args[3]);
        Assert.Equal(7L, Assert.Single(_service.Of("complete")).Args[0]);
        Assert.Contains(_view.Pushed, x => x.Agent == Me && x.Balance == 90);
        Assert.Contains(_view.Pushed, x => x.Agent == Them && x.Balance == 110);
    }

    [Fact]
    public async Task Failed_delivery_returns_at_once()
    {
        _service.Queue("hold", new MoneyAnswer("ok", 90, null, 1, 7));
        _service.Queue("return", new MoneyAnswer("ok", null, 100, 2, 7));

        var bought = await _actions.BuyAsync(P, Part(), ThemWho, 1, 10, () => false);

        Assert.False(bought);
        Assert.Empty(_service.Of("complete"));
        Assert.Single(_service.Of("return"));
        Assert.Equal((Me, 100L, (BalanceNote?)null), _view.Pushed[^1]);
    }

    [Fact]
    public async Task Delivery_that_throws_is_returned()
    {
        _service.Queue("hold", new MoneyAnswer("ok", 90, null, 1, 7));

        var bought = await _actions.BuyAsync(P, Part(), ThemWho, 1, 10, () => throw new InvalidOperationException("scene went away"));

        Assert.False(bought);
        Assert.Single(_service.Of("return"));
    }

    [Fact]
    public async Task Refused_hold_does_not_deliver()
    {
        _service.Queue("hold", new MoneyAnswer("not_enough"));
        var delivered = false;

        var bought = await _actions.BuyAsync(P, Part(), ThemWho, 1, 10, () => delivered = true);

        Assert.False(bought);
        Assert.False(delivered);
        Assert.Equal("You don't have enough coins for that.", Assert.Single(_view.Told).Text);
    }

    [Fact]
    public async Task Complete_is_retried_with_the_same_txn()
    {
        _service.Queue("hold", new MoneyAnswer("ok", 90, null, 1, 7));
        _service.Queue("complete", MoneyAnswer.Unavailable, MoneyAnswer.Unavailable, new MoneyAnswer("ok", null, 110, 2, 7));

        Assert.True(await _actions.BuyAsync(P, Part(), ThemWho, 1, 10, () => true));

        var completes = _service.Of("complete").ToList();
        Assert.Equal(3, completes.Count);
        Assert.Single(completes.Select(c => c.Txn).Distinct());
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], _view.Delays);
        Assert.Empty(_view.Errors);
    }

    [Fact]
    public async Task Complete_that_never_lands_is_logged_with_the_hold()
    {
        _service.Queue("hold", new MoneyAnswer("ok", 90, null, 1, 7));
        _service.Answer = m => m == "complete" ? MoneyAnswer.Unavailable : new MoneyAnswer("ok");

        await _actions.BuyAsync(P, Part(), ThemWho, 1, 10, () => true);

        Assert.Equal(4, _service.Of("complete").Count());
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(9)], _view.Delays);
        Assert.Contains("7", Assert.Single(_view.Errors));
    }

    [Fact]
    public async Task Free_sale_skips_money()
    {
        var delivered = false;

        Assert.True(await _actions.BuyAsync(P, Part(salePrice: 0), ThemWho, 1, 0, () => delivered = true));

        Assert.True(delivered);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task Buying_your_own_object_skips_money()
    {
        Assert.True(await _actions.BuyAsync(P, Part(owner: Me), new MoneyWho(Me, ""), 1, 10, () => true));

        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task Group_owned_sale_is_refused()
    {
        var delivered = false;

        Assert.False(await _actions.BuyAsync(P, Part(owner: Group, group: Group), new MoneyWho(Group, ""), 1, 10, () => delivered = true));

        Assert.False(delivered);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task Land_hold_returns_when_owner_did_not_change()
    {
        _service.Queue("hold", new MoneyAnswer("ok", 50, null, 1, 9));

        var hold = await _actions.HoldLandAsync(P, ThemWho, 50, 3);
        Assert.Equal(9, hold);
        Assert.Equal("land", Assert.Single(_service.Of("hold")).Args[0]);

        await _actions.SettleLandAsync(P, ThemWho, hold!.Value, newOwnerConfirmed: false);

        Assert.Equal(9L, Assert.Single(_service.Of("return")).Args[0]);
        Assert.Empty(_service.Of("complete"));
    }

    [Fact]
    public async Task Land_hold_completes_when_owner_changed()
    {
        _service.Queue("hold", new MoneyAnswer("ok", 50, null, 1, 9));
        _service.Queue("complete", new MoneyAnswer("ok", null, 150, 2, 9));

        var hold = await _actions.HoldLandAsync(P, ThemWho, 50, 3);
        await _actions.SettleLandAsync(P, ThemWho, hold!.Value, newOwnerConfirmed: true);

        Assert.Single(_service.Of("complete"));
        Assert.Contains(_view.Pushed, x => x.Agent == Them && x.Balance == 150);
    }

    [Fact]
    public async Task Free_land_holds_nothing()
    {
        Assert.Equal(0, await _actions.HoldLandAsync(P, ThemWho, 0, 3));
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task Refused_land_hold_tells_the_buyer()
    {
        _service.Queue("hold", new MoneyAnswer("not_enough"));

        Assert.Null(await _actions.HoldLandAsync(P, ThemWho, 50, 3));
        Assert.Equal("You don't have enough coins for that.", Assert.Single(_view.Told).Text);
    }

    [Fact]
    public void Pass_moves_coins_synchronously()
    {
        _service.Queue("transfer", new MoneyAnswer("ok", 95, 105, 1));

        Assert.True(_actions.MovePass(P, ThemWho, 5));

        Assert.Equal("parcel_pass", Assert.Single(_service.Of("transfer")).Args[0]);
        _service.Queue("transfer", new MoneyAnswer("not_enough"));
        Assert.False(_actions.MovePass(P, ThemWho, 5));
    }

    [Fact]
    public void Payout_zero_is_invalid_without_call()
    {
        Assert.False(_actions.Payout(Me, ThemWho, 0, PartId, "slot", Guid.NewGuid(), out var reason));

        Assert.Equal("INVALID_AMOUNT", reason);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void Payout_maps_refusals_to_script_reasons()
    {
        _service.Queue("payout", new MoneyAnswer("not_allowed"));

        Assert.False(_actions.Payout(Me, ThemWho, 5, PartId, "slot", Guid.NewGuid(), out var reason));

        Assert.Equal("TRANSFERS_DISABLED", reason);
    }

    [Fact]
    public void Payout_ok_pushes_both()
    {
        _service.Queue("payout", new MoneyAnswer("ok", 95, 105, 1));
        var txn = Guid.NewGuid();

        Assert.True(_actions.Payout(Me, ThemWho, 5, PartId, "slot", txn, out var reason));

        Assert.Equal("", reason);
        Assert.Equal(txn, Assert.Single(_service.Of("payout")).Txn);
        Assert.Contains(_view.Pushed, x => x.Agent == Them && x.Balance == 105 && x.Note!.TransactionType == Type("ObjectPays"));
    }

    [Fact]
    public void Payout_without_txn_gets_one()
    {
        _actions.Payout(Me, ThemWho, 5, PartId, "slot", Guid.Empty, out _);

        Assert.NotEqual(Guid.Empty, Assert.Single(_service.Of("payout")).Txn);
    }

    [Fact]
    public void Payout_to_visitor_needs_visitor_wallets()
    {
        _economy = _economy with { VisitorWallets = false };

        Assert.False(_actions.Payout(Me, new MoneyWho(Them, "http://other.grid:8002/"), 5, PartId, "slot", Guid.NewGuid(), out var reason));

        Assert.Equal("LINDENDOLLAR_ENTITYDOESNOTEXIST", reason);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void Covered_reads_a_fresh_balance()
    {
        _service.Queue("balance", new MoneyAnswer("ok", 10), new MoneyAnswer("ok", 10), MoneyAnswer.Unavailable);

        Assert.True(_actions.Covered(P, 10));
        Assert.False(_actions.Covered(P, 11));
        Assert.False(_actions.Covered(P, 1));
        Assert.True(_actions.Covered(P, 0));
        Assert.Equal(3, _service.Of("balance").Count());
    }

    [Fact]
    public void Uncollected_charge_is_logged_not_retried()
    {
        _service.Queue("charge", new MoneyAnswer("not_enough"));

        _actions.Charge(P, "upload_fee", 3, null);

        Assert.Single(_service.Of("charge"));
        var line = Assert.Single(_view.Warnings);
        Assert.Equal($"Uncollected upload_fee of 3 from {Me}: not_enough", line);
        Assert.DoesNotContain(P.SessionId.ToString(), line);
        Assert.DoesNotContain(P.SecureSessionId.ToString(), line);
    }

    [Fact]
    public void Zero_charge_makes_no_call()
    {
        _actions.Charge(P, "upload_fee", 0, null);

        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void Charge_carries_who_is_paid_and_the_region()
    {
        _service.Queue("charge", new MoneyAnswer("ok", 90, 10));

        _actions.Charge(P, "group_join_fee", 10, Them);

        var call = Assert.Single(_service.Of("charge"));
        Assert.Equal("group_join_fee", call.Args[0]);
        Assert.Equal((Guid?)Them, call.Args[3]);
        Assert.Equal(Region, call.Args[4]); // the service checks the payer is in this region (money design §8.11)
        Assert.Contains(_view.Pushed, x => x.Agent == Me && x.Balance == 90 && x.Note!.TransactionType == Type("GroupJoin"));
    }

    [Fact]
    public async Task Entry_push_retries_once_on_wrong_region()
    {
        _service.Queue("balance", new MoneyAnswer("wrong_region"), new MoneyAnswer("ok", 42));

        await _actions.PushEntryBalanceAsync(P);

        Assert.Equal([TimeSpan.FromSeconds(2)], _view.Delays);
        Assert.Equal((Me, 42L, (BalanceNote?)null), Assert.Single(_view.Pushed));
    }

    [Fact]
    public async Task Entry_push_stays_silent_after_two_wrong_regions()
    {
        _service.Queue("balance", new MoneyAnswer("wrong_region"), new MoneyAnswer("wrong_region"));

        await _actions.PushEntryBalanceAsync(P);

        Assert.Empty(_view.Pushed);
        Assert.Equal(2, _service.Of("balance").Count());
    }

    [Fact]
    public async Task Entry_push_never_pushes_a_failed_balance()
    {
        _service.Queue("balance", MoneyAnswer.Unavailable);

        await _actions.PushEntryBalanceAsync(P);

        Assert.Empty(_view.Pushed);
        Assert.Empty(_view.Delays);
    }

    [Fact]
    public async Task Holding_balance_never_pushed()
    {
        _service.Queue("hold", new MoneyAnswer("ok", 60, null, 1, 7));
        _service.Queue("complete", new MoneyAnswer("ok", null, 140, 2, 7));

        await _actions.BuyAsync(P, Part(salePrice: 40), ThemWho, 1, 40, () => true);

        Assert.Equal([(Me, 60L), (Them, 140L)], _view.Pushed.Select(x => (x.Agent, x.Balance)));
    }

    [Fact]
    public void Actions_type_lives_in_the_module()
    {
        Assert.Equal("GridGateMoney", typeof(MoneyActions).Assembly.GetName().Name);
        Assert.NotNull(typeof(MoneyActions).GetMethod("PayAvatarAsync", BindingFlags.Public | BindingFlags.Instance));
    }
}
