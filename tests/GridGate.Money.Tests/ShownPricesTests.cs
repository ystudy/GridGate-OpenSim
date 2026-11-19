using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>The prices a viewer was last shown for a group's membership or a parcel's pass.</summary>
public class ShownPricesTests
{
    private static readonly UUID Me = TestHelpers.ParseTail(0x701), Other = TestHelpers.ParseTail(0x702);
    private static readonly UUID Group = TestHelpers.ParseTail(0x703), Parcel = TestHelpers.ParseTail(0x704);

    [Fact]
    public void A_price_shown_is_charged_and_one_not_shown_is_refused()
    {
        var shown = new ShownPrices();

        Assert.False(shown.MayCharge(Me, Group, 20));
        shown.Remember(Me, Group, 20);
        Assert.True(shown.MayCharge(Me, Group, 20));
    }

    [Fact]
    public void A_higher_price_is_refused_once_and_the_next_click_may_charge_it()
    {
        var shown = new ShownPrices();
        shown.Remember(Me, Group, 20);

        Assert.False(shown.MayCharge(Me, Group, 5000));
        Assert.True(shown.MayCharge(Me, Group, 5000));
    }

    [Fact]
    public void A_lower_price_may_be_charged_and_becomes_the_most_a_later_click_can_charge()
    {
        var shown = new ShownPrices();
        shown.Remember(Me, Group, 20);

        Assert.True(shown.MayCharge(Me, Group, 5));
        Assert.False(shown.MayCharge(Me, Group, 20));
    }

    [Fact]
    public void Free_is_always_charged_as_free()
    {
        var shown = new ShownPrices();

        Assert.True(shown.MayCharge(Me, Group, 0));
    }

    [Fact]
    public void A_price_is_shown_to_one_person_for_one_thing()
    {
        var shown = new ShownPrices();
        shown.Remember(Me, Group, 20);

        Assert.False(shown.MayCharge(Other, Group, 20));
        Assert.False(shown.MayCharge(Me, Parcel, 20));
    }

    [Fact]
    public void A_price_shown_more_than_ten_minutes_ago_is_not_enough()
    {
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        var shown = new ShownPrices(() => now);
        shown.Remember(Me, Group, 20);

        now += ShownPrices.Lifetime;
        Assert.True(shown.MayCharge(Me, Group, 20));

        shown.Remember(Me, Group, 20);
        now += ShownPrices.Lifetime + TimeSpan.FromSeconds(1);
        Assert.False(shown.MayCharge(Me, Group, 20));
    }

    [Fact]
    public void When_full_the_oldest_price_is_dropped()
    {
        var shown = new ShownPrices(capacity: 2);
        shown.Remember(Me, Group, 1);
        shown.Remember(Me, Parcel, 2);

        shown.Remember(Other, Group, 3);

        Assert.False(shown.MayCharge(Me, Group, 1));
    }
}
