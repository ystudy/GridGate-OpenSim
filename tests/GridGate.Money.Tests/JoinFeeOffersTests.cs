using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Tests.Common;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>The join fees a groups module remembers for the invitations it showed them in.</summary>
public class JoinFeeOffersTests
{
    private static readonly UUID A = TestHelpers.ParseTail(0x601), B = TestHelpers.ParseTail(0x602), C = TestHelpers.ParseTail(0x603);

    [Fact]
    public void A_fee_is_found_by_its_invitation_and_replaced_by_a_newer_one()
    {
        var offers = new JoinFeeOffers();

        Assert.Null(offers.Find(A));
        offers.Remember(A, 20);
        Assert.Equal(20, offers.Find(A));
        offers.Remember(A, 5000);
        Assert.Equal(5000, offers.Find(A));
        Assert.Null(offers.Find(B));
    }

    [Fact]
    public void A_forgotten_fee_is_not_found()
    {
        var offers = new JoinFeeOffers();
        offers.Remember(A, 20);

        offers.Forget(A);

        Assert.Null(offers.Find(A));
    }

    [Fact]
    public void A_fee_older_than_fourteen_days_is_not_found()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        var offers = new JoinFeeOffers(() => now);
        offers.Remember(A, 20);

        now += JoinFeeOffers.Lifetime;
        Assert.Equal(20, offers.Find(A));

        now += TimeSpan.FromSeconds(1);
        Assert.Null(offers.Find(A));
    }

    [Fact]
    public void When_full_the_oldest_fee_is_dropped()
    {
        var offers = new JoinFeeOffers(capacity: 2);
        offers.Remember(A, 1);
        offers.Remember(B, 2);

        offers.Remember(C, 3);

        Assert.Null(offers.Find(A));
        Assert.Equal(2, offers.Find(B));
        Assert.Equal(3, offers.Find(C));
    }

    [Fact]
    public void Remembering_again_after_forgetting_does_not_cost_a_newer_fee_its_place()
    {
        var offers = new JoinFeeOffers(capacity: 2);
        offers.Remember(A, 1);
        offers.Forget(A);
        offers.Remember(B, 2);
        offers.Remember(A, 1);

        offers.Remember(C, 3);

        Assert.Null(offers.Find(B));
        Assert.Equal(1, offers.Find(A));
        Assert.Equal(3, offers.Find(C));
    }
}
