using GridGate.Region.Traffic;
using Xunit;

namespace GridGate.Traffic.Tests;

/// <summary>
/// The counting rules (spec 2026-11-21-featured-places-design, "Visits"): each avatar counts once per parcel per UTC day,
/// and the number fades with a 30-day mean life, so it reads as "recent visits".
/// </summary>
public sealed class ParcelTrafficTests
{
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);
    private static readonly DateTimeOffset Noon = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Parcel = Guid.Parse("0000000a-0000-0000-0000-000000000001");
    private static readonly Guid Ann = Guid.Parse("0000000a-0000-0000-0000-0000000000a1");
    private static readonly Guid Bob = Guid.Parse("0000000a-0000-0000-0000-0000000000b2");

    [Fact]
    public void First_visit_counts_one()
    {
        var traffic = new ParcelTraffic(Month);

        Assert.True(traffic.Visit(Parcel, Ann, Noon, seed: 0));

        Assert.Equal(1, traffic.ValueOf(Parcel, Noon, seed: 0), 6);
    }

    [Fact]
    public void Same_agent_same_day_counts_once()
    {
        var traffic = new ParcelTraffic(Month);
        traffic.Visit(Parcel, Ann, Noon, 0);

        Assert.False(traffic.Visit(Parcel, Ann, Noon.AddHours(11), 0));

        Assert.Equal(Math.Exp(-11.0 / (30 * 24)), traffic.ValueOf(Parcel, Noon.AddHours(11), 0), 6); // one visit, faded 11 hours
    }

    [Fact]
    public void Same_agent_next_utc_day_counts_again()
    {
        var traffic = new ParcelTraffic(Month);
        traffic.Visit(Parcel, Ann, Noon, 0);

        Assert.True(traffic.Visit(Parcel, Ann, Noon.AddHours(12), 0)); // 00:00 UTC the next day
    }

    [Fact]
    public void Other_agent_counts()
    {
        var traffic = new ParcelTraffic(Month);
        traffic.Visit(Parcel, Ann, Noon, 0);

        Assert.True(traffic.Visit(Parcel, Bob, Noon, 0));

        Assert.Equal(2, traffic.ValueOf(Parcel, Noon, 0), 6);
    }

    [Fact]
    public void Value_fades_with_30_day_mean_life() =>
        Assert.Equal(36.79, ParcelTraffic.Decayed(100, Noon, Noon + Month, Month), 2);

    [Fact]
    public void Visit_decays_before_adding()
    {
        var traffic = new ParcelTraffic(Month);
        traffic.Visit(Parcel, Ann, Noon, seed: 9); // the stored 9 from before a restart, plus this visit

        traffic.Visit(Parcel, Bob, Noon + Month, 0);

        Assert.Equal(10 * Math.Exp(-1) + 1, traffic.ValueOf(Parcel, Noon + Month, 0), 6);
    }

    [Fact]
    public void Untouched_parcel_fades_from_its_seed()
    {
        var traffic = new ParcelTraffic(Month);

        var settled = traffic.Settle(Parcel, Noon, seed: 50);

        Assert.Equal(50, settled, 6);
        Assert.Equal(50 * Math.Exp(-1), traffic.Settle(Parcel, Noon + Month, seed: 0), 6);
    }
}
