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

    private MoneyActions ActionsWithFee(long fee) =>
        new(_service, Region, () => new EconomyInfo("C$", 0, fee, true, true, 1_000_000), _view);
}
