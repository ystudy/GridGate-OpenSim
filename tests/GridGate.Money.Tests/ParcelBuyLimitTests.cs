using GridGate.Region.Money;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GridGate.Money.Tests;

public sealed class ParcelBuyLimitTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private ParcelBuyLimit Limit() => new(_time);

    [Fact]
    public void Five_in_a_minute_pass_and_the_sixth_is_refused()
    {
        var limit = Limit();

        for (var i = 0; i < ParcelBuyLimit.PerWindow; i++) Assert.True(limit.TryTake(A));

        Assert.False(limit.TryTake(A));
    }

    [Fact]
    public void A_minute_later_it_passes_again()
    {
        var limit = Limit();
        for (var i = 0; i < ParcelBuyLimit.PerWindow; i++) limit.TryTake(A);
        Assert.False(limit.TryTake(A));

        _time.Advance(ParcelBuyLimit.Window + TimeSpan.FromSeconds(1));

        Assert.True(limit.TryTake(A));
    }

    [Fact]
    public void Agents_are_counted_apart()
    {
        var limit = Limit();
        for (var i = 0; i < ParcelBuyLimit.PerWindow; i++) limit.TryTake(A);

        Assert.True(limit.TryTake(B));
    }

    [Fact]
    public void Forget_drops_an_agent()
    {
        var limit = Limit();
        for (var i = 0; i < ParcelBuyLimit.PerWindow; i++) limit.TryTake(A);

        limit.Forget(A);

        Assert.True(limit.TryTake(A));
    }
}
