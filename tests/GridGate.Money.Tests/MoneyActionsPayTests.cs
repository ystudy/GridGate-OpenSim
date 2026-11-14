using GridGate.Region.Money;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>The refusals the region module makes on its own, before any service call (money plan 2, task 3).</summary>
public sealed class MoneyActionsPayTests
{
    private static readonly Guid Me = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Other = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Group = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid Root = Guid.Parse("d0000000-0000-0000-0000-00000000000d");

    private static PartInfo Part(int[]? payPrice = null, Guid? owner = null, Guid? group = null, bool isRoot = true, byte saleType = 1,
        int salePrice = 10, bool contents = true) =>
        new(isRoot ? Root : Guid.NewGuid(), Root, isRoot, owner ?? Other, group ?? Guid.Empty, "thing", payPrice ?? [-2, -2, -2, -2, -2], saleType,
            salePrice, contents);

    [Fact]
    public void Forged_source_is_refused()
    {
        Assert.Equal(Refusal.ForgedSource, MoneyRules.CheckPayAvatar(Me, Other, 5));
        Assert.Equal(Refusal.ForgedSource, MoneyRules.CheckPayObject(Me, Other, 5, Part()));
        Assert.Equal(Refusal.None, MoneyRules.CheckPayAvatar(Me, Me, 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_is_refused(int amount)
    {
        Assert.Equal(Refusal.BadAmount, MoneyRules.CheckPayAvatar(Me, Me, amount));
        Assert.Equal(Refusal.BadAmount, MoneyRules.CheckPayObject(Me, Me, amount, Part()));
    }

    [Fact]
    public void Open_box_accepts_any_amount()
    {
        Assert.True(MoneyRules.PayPriceAllows([-2, -2, -2, -2, -2], 37));
        Assert.Equal(Refusal.None, MoneyRules.CheckPayObject(Me, Me, 37, Part()));
    }

    [Fact]
    public void Hidden_box_accepts_only_buttons()
    {
        int[] price = [-1, 10, 25, -1, -1];

        Assert.True(MoneyRules.PayPriceAllows(price, 10));
        Assert.True(MoneyRules.PayPriceAllows(price, 25));
        Assert.False(MoneyRules.PayPriceAllows(price, 11));
        Assert.False(MoneyRules.PayPriceAllows(price, 1));
        Assert.Equal(Refusal.NotPayPrice, MoneyRules.CheckPayObject(Me, Me, 11, Part(price)));
    }

    [Fact]
    public void Hidden_box_accepts_default_buttons()
    {
        int[] price = [-1, -2, -2, -2, -2];

        foreach (var amount in new[] { 1, 5, 10, 20 })
            Assert.True(MoneyRules.PayPriceAllows(price, amount), $"{amount}");
        Assert.False(MoneyRules.PayPriceAllows(price, 2));
    }

    [Fact]
    public void Hidden_buttons_take_nothing()
    {
        Assert.False(MoneyRules.PayPriceAllows([-1, -1, 0, -1, -1], 1));
        Assert.False(MoneyRules.PayPriceAllows([-1, -1, 0, -1, -1], 0));
        Assert.False(MoneyRules.PayPriceAllows([], 5)); // a malformed price allows nothing
    }

    [Fact]
    public void Group_owned_object_is_refused()
    {
        var part = Part(owner: Group, group: Group);

        Assert.True(part.GroupOwned);
        Assert.Equal(Refusal.GroupOwned, MoneyRules.CheckPayObject(Me, Me, 5, part));
        Assert.Equal(Refusal.GroupOwned, MoneyRules.CheckBuy(part, 1, 10));
        Assert.False(Part(group: Group).GroupOwned); // set to a group, owned by a person
    }

    [Fact]
    public void Buy_needs_root_matching_type_and_price()
    {
        Assert.Equal(Refusal.NotRoot, MoneyRules.CheckBuy(Part(isRoot: false), 1, 10));
        Assert.Equal(Refusal.NotForSale, MoneyRules.CheckBuy(Part(saleType: 0), 0, 10));
        Assert.Equal(Refusal.NotForSale, MoneyRules.CheckBuy(Part(saleType: 2), 1, 10));
        Assert.Equal(Refusal.PriceChanged, MoneyRules.CheckBuy(Part(salePrice: 12), 1, 10));
        Assert.Equal(Refusal.BadAmount, MoneyRules.CheckBuy(Part(salePrice: -1), 1, -1));
        Assert.Equal(Refusal.None, MoneyRules.CheckBuy(Part(), 1, 10));
        Assert.Equal(Refusal.None, MoneyRules.CheckBuy(Part(salePrice: 0), 1, 0)); // free things are bought for nothing
    }

    [Fact]
    public void Contents_sale_needs_something_transferable()
    {
        Assert.Equal(Refusal.NothingToSell, MoneyRules.CheckBuy(Part(saleType: 3, contents: false), 3, 10));
        Assert.Equal(Refusal.None, MoneyRules.CheckBuy(Part(saleType: 3), 3, 10));
        Assert.Equal(Refusal.UnknownType, MoneyRules.CheckBuy(Part(saleType: 4), 4, 10));
    }

    [Theory]
    [InlineData("not_enough", "LINDENDOLLAR_INSUFFICIENTFUNDS")]
    [InlineData("no_wallet", "LINDENDOLLAR_ENTITYDOESNOTEXIST")]
    [InlineData("not_allowed", "TRANSFERS_DISABLED")]
    [InlineData("over_daily_limit", "THROTTLED")]
    [InlineData("bad_amount", "INVALID_AMOUNT")]
    [InlineData("unavailable", "SERVICE_ERROR")]
    [InlineData("frozen", "SERVICE_ERROR")]
    [InlineData("something new", "SERVICE_ERROR")]
    public void Script_reasons_map(string code, string reason) => Assert.Equal(reason, MoneyRules.ScriptReason(code));

    [Fact]
    public void Viewer_texts_are_the_spec_ones()
    {
        Assert.Equal("Group-owned objects can't take payments on this grid. Ask the owner to set it to a person.", MoneyRules.Message(Refusal.GroupOwned));
        Assert.Equal("Group-owned land can't be sold for coins on this grid.", MoneyRules.GroupLandText);
        Assert.Equal("You don't have enough coins for that.", MoneyRules.Message("not_enough"));
        Assert.Equal("Payments are paused on this grid right now.", MoneyRules.Message("frozen"));
        Assert.Equal("Your coins are frozen. Please contact the grid's staff.", MoneyRules.Message("account_frozen"));
        Assert.Equal("Money is unavailable right now. Please try again later.", MoneyRules.Message("unavailable"));
        Assert.Equal("That person can't receive coins on this grid.", MoneyRules.Message("no_wallet"));
        Assert.Equal("Please log in again to use money here.", MoneyRules.Message("no_session"));
        Assert.Equal("Please log in again to use money here.", MoneyRules.Message("wrong_region"));
        Assert.Equal("That amount isn't one this object accepts.", MoneyRules.Message(Refusal.NotPayPrice));
        Assert.Equal("This object's sale details changed. Please try again.", MoneyRules.Message(Refusal.NotForSale));
        Assert.Equal("This object's sale details changed. Please try again.", MoneyRules.Message(Refusal.PriceChanged));
        Assert.Equal("This object has nothing to sell.", MoneyRules.Message(Refusal.NothingToSell));
        Assert.Equal("Visitors can't pay or be paid on this grid.", MoneyRules.VisitorsOffText);
    }

    [Fact]
    public void Messages_fit_250_chars()
    {
        string[] codes = ["ok", "not_enough", "frozen", "account_frozen", "unavailable", "bad_amount", "not_allowed", "over_daily_limit", "unknown_hold",
            "hold_closed", "no_wallet", "duplicate_reference", "duplicate_txn", "no_session", "wrong_region", "bad_request", "anything"];
        foreach (var r in Enum.GetValues<Refusal>().Where(r => r != Refusal.None))
            Assert.InRange(MoneyRules.Message(r).Length, 1, 250);
        foreach (var c in codes.Where(c => c != "ok"))
            Assert.InRange(MoneyRules.Message(c).Length, 1, 250);
        Assert.InRange(MoneyRules.GroupLandText.Length, 1, 250);
        Assert.InRange(MoneyRules.VisitorsOffText.Length, 1, 250);
    }
}
