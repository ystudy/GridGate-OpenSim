using GridGate.Region.Money;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>The whole-branch review's findings, each pinned (money plan 2, final review).</summary>
public sealed class MoneyActionsReviewTests
{
    private static readonly Guid Me = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Other = Guid.Parse("b1000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Seller = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Group = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly Guid Root = Guid.Parse("d0000000-0000-0000-0000-00000000000d");
    private static readonly Payer A = new(Me, "", Guid.NewGuid(), Guid.NewGuid());
    private static readonly Payer B = new(Other, "", Guid.NewGuid(), Guid.NewGuid());
    private static readonly MoneyWho SellerWho = new(Seller, "");

    private readonly FakeMoneyService _service = new();
    private readonly FakeView _view = new();
    private readonly MoneyActions _actions;

    public MoneyActionsReviewTests() =>
        _actions = new MoneyActions(_service, Region, () => new EconomyInfo("C$", 0, 0, true, true, 1_000_000), _view);

    private static PartInfo Part(Guid? owner = null, int salePrice = 10, byte saleType = 1, Guid? group = null) =>
        new(Root, Root, true, owner ?? Seller, group ?? Guid.Empty, "dress", [-2, -2, -2, -2, -2], saleType, salePrice, true);

    [Fact]
    public async Task Two_buys_of_one_original_pay_once()
    {
        // The first buyer's hold is held open; the second clicks Buy meanwhile. Delivery gives the object to the first.
        var owner = Seller;
        var release = new TaskCompletionSource();
        var holds = 0;
        _service.Gate = m => m == "hold" && Interlocked.Increment(ref holds) == 1 ? release.Task : Task.CompletedTask;
        _service.Answer = m => m == "hold" ? new MoneyAnswer("ok", 90, null, 1, 7) : new MoneyAnswer("ok", null, 110, 2, 7);
        PartInfo Now() => Part(owner: owner);

        var first = _actions.BuyAsync(A, Now(), SellerWho, 1, 10, () => { owner = Me; return true; }, Now);
        var second = _actions.BuyAsync(B, Now(), SellerWho, 1, 10, () => { owner = Other; return true; }, Now);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(_service.Of("hold")); // the second waits for the first
        release.SetResult();

        Assert.True(await first);
        Assert.False(await second);
        Assert.Equal(Me, owner);
        Assert.Single(_service.Of("hold"));
        Assert.Single(_service.Of("complete"));
        Assert.Contains(_view.Told, t => t.Agent == Other);
    }

    [Fact]
    public async Task Sale_changed_during_the_hold_returns_it()
    {
        var price = 10;
        _service.Gate = m => { if (m == "hold") price = 12; return Task.CompletedTask; };
        _service.Queue("hold", new MoneyAnswer("ok", 90, null, 1, 7));
        var delivered = false;

        var bought = await _actions.BuyAsync(A, Part(), SellerWho, 1, 10, () => delivered = true, () => Part(salePrice: price));

        Assert.False(bought);
        Assert.False(delivered);
        Assert.Single(_service.Of("return"));
        Assert.Empty(_service.Of("complete"));
    }

    [Fact]
    public async Task Object_gone_before_the_hold_is_refused()
    {
        var bought = await _actions.BuyAsync(A, Part(), SellerWho, 1, 10, () => true, () => null);

        Assert.False(bought);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task Land_hold_gives_up_after_its_limit()
    {
        _service.Gate = _ => new TaskCompletionSource().Task; // the service never answers

        var hold = await _actions.HoldLandAsync(A, SellerWho, 50, 3, TimeSpan.FromMilliseconds(100));

        Assert.Null(hold);
        Assert.Equal("Money is unavailable right now. Please try again later.", Assert.Single(_view.Told).Text);
    }

    [Fact]
    public async Task A_land_hold_that_arrives_after_the_limit_is_returned_to_the_buyer()
    {
        var release = new TaskCompletionSource();
        _service.Gate = m => m == "hold" ? release.Task : Task.CompletedTask;
        _service.Answer = m => m == "hold" ? new MoneyAnswer("ok", 50, null, 1, 7) : new MoneyAnswer("ok", 100, 0, 2, 7);

        Assert.Null(await _actions.HoldLandAsync(A, SellerWho, 50, 3, TimeSpan.FromMilliseconds(100)));
        Assert.Empty(_service.Of("return"));
        release.SetResult();

        var returned = await Eventually(() => _service.Of("return").FirstOrDefault());
        Assert.Equal(7L, returned.Args[0]);
        Assert.Empty(_service.Of("complete"));
        Assert.DoesNotContain(_view.Pushed, p => p.Balance == 50); // the buyer never saw the held balance as theirs to keep
    }

    [Fact]
    public async Task A_land_hold_that_arrives_late_without_a_hold_returns_nothing()
    {
        var release = new TaskCompletionSource();
        _service.Gate = m => m == "hold" ? release.Task : Task.CompletedTask;
        _service.Answer = _ => new MoneyAnswer("not_enough");

        Assert.Null(await _actions.HoldLandAsync(A, SellerWho, 50, 3, TimeSpan.FromMilliseconds(100)));
        release.SetResult();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Empty(_service.Of("return"));
    }

    [Fact]
    public async Task Land_holds_fail_fast_for_thirty_seconds_after_one_ran_out_of_time()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var actions = new MoneyActions(_service, Region, () => new EconomyInfo("C$", 0, 0, true, true, 1_000_000), _view) { Time = time };
        _service.Gate = _ => new TaskCompletionSource().Task;
        Assert.False(actions.LandPaused);

        Assert.Null(await actions.HoldLandAsync(A, SellerWho, 50, 3, TimeSpan.FromMilliseconds(100)));

        Assert.True(actions.LandPaused);
        var calls = _service.Of("hold").Count();
        Assert.Null(await actions.HoldLandAsync(B, SellerWho, 50, 4, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(calls, _service.Of("hold").Count());
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.True(actions.LandPaused);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(actions.LandPaused);
    }

    private static async Task<T> Eventually<T>(Func<T?> read) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (read() is not { } found)
        {
            Assert.True(DateTime.UtcNow < deadline, "It never happened.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return read()!;
    }

    [Theory]
    [InlineData("wrong_region")]
    [InlineData("unknown_hold")]
    [InlineData("hold_closed")]
    public async Task A_refused_complete_is_logged_with_the_hold(string code)
    {
        _service.Queue("hold", new MoneyAnswer("ok", 90, null, 1, 7));
        _service.Queue("complete", new MoneyAnswer(code));

        await _actions.BuyAsync(A, Part(), SellerWho, 1, 10, () => true);

        var line = Assert.Single(_view.Errors);
        Assert.Contains("7", line);
        Assert.Contains(code, line);
    }

    [Fact]
    public void Script_payouts_refuse_group_owned_objects_and_strangers()
    {
        Assert.Equal("GROUP_OWNED", MoneyRules.CheckPayout(Part(owner: Group, group: Group), Group));
        Assert.Equal("SERVICE_ERROR", MoneyRules.CheckPayout(Part(), Me)); // the source isn't the object's owner
        Assert.Null(MoneyRules.CheckPayout(Part(), Seller));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Group_land_is_refused(bool parcelGroupOwned, bool buyForGroup) =>
        Assert.True(MoneyRules.LandGroupRefused(parcelGroupOwned, buyForGroup));

    [Fact]
    public void Personal_land_is_not_refused() => Assert.False(MoneyRules.LandGroupRefused(false, false));

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100000)]
    public void Classified_fee_is_the_grids_not_the_viewers(int typedByViewer)
    {
        // Found in the live checks: OpenSim passes the listing price the viewer typed; the fee is the owner's setting.
        var actions = new MoneyActions(_service, Region, () => new EconomyInfo("C$", 0, 3, true, true, 1_000_000), _view);
        _service.Queue("charge", new MoneyAnswer("ok", 97));

        actions.ChargeClassified(A, typedByViewer);

        var call = Assert.Single(_service.Of("charge"));
        Assert.Equal("classified_fee", call.Args[0]);
        Assert.Equal(3L, call.Args[2]);
    }

    [Fact]
    public void Free_classifieds_cost_nothing()
    {
        ActionsWithFee(0).ChargeClassified(A, 50);

        Assert.Empty(_service.Calls);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Classified_check_uses_the_grids_fee(long balance, bool covered)
    {
        // Review of c11e7626: OpenSim checked the price the viewer typed, so a typed 0 listed for nothing.
        _service.Queue("balance", new MoneyAnswer("ok", balance));

        Assert.Equal(covered, ActionsWithFee(3).ClassifiedCovered(A));
    }

    [Fact]
    public void Free_classifieds_need_no_coins()
    {
        Assert.True(ActionsWithFee(0).ClassifiedCovered(A));
        Assert.Empty(_service.Calls);
    }

    private MoneyActions ActionsWithFee(long fee) =>
        new(_service, Region, () => new EconomyInfo("C$", 0, fee, true, true, 1_000_000), _view);

    // ---- money review 2026-11-15 --------------------------------------------------------------------

    private MoneyActions BeforeEconomy() => new(_service, Region, () => null, _view);

    [Fact]
    public void Before_the_first_economy_answer_no_upload_is_free()
    {
        // R-8: until the service answered, uploads were offered for nothing.
        Assert.False(BeforeEconomy().UploadCovered(A, 0));

        Assert.Empty(_service.Calls);
        Assert.Equal(MoneyRules.NoEconomyText, Assert.Single(_view.Told).Text);
    }

    [Fact]
    public void Before_the_first_economy_answer_no_classified_is_free()
    {
        Assert.False(BeforeEconomy().ClassifiedCovered(A));
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void Before_the_first_economy_answer_visitors_get_no_payouts()
    {
        var paid = BeforeEconomy().Payout(Me, new MoneyWho(Other, "http://other.example:8002"), 5, Root, "box", Guid.NewGuid(), out var reason);

        Assert.False(paid);
        Assert.Equal("LINDENDOLLAR_ENTITYDOESNOTEXIST", reason);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public void Once_the_fee_is_known_an_upload_checks_the_balance()
    {
        _service.Queue("balance", new MoneyAnswer("ok", 10));

        Assert.True(ActionsWithFee(0).UploadCovered(A, 10));
        Assert.Single(_service.Of("balance"));
    }

    [Fact]
    public void A_pass_to_group_land_is_refused_by_the_module()
    {
        // R-9: OpenSim's land module refuses this first today; the module no longer relies on it.
        Assert.False(_actions.MovePass(A, new MoneyWho(Group, ""), 5, groupLand: true));

        Assert.Empty(_service.Calls);
        Assert.Equal(MoneyRules.GroupPassText, Assert.Single(_view.Told).Text);
    }
}
