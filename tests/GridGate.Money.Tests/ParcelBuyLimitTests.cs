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
    public void A_closed_client_keeps_its_count_until_it_expires()
    {
        var limit = Limit();
        for (var i = 0; i < ParcelBuyLimit.PerWindow; i++) limit.TryTake(A);

        limit.Forget(A); // the client closed (a teleport out and back)

        Assert.False(limit.TryTake(A));
        Assert.Equal(1, limit.Count);
    }

    [Fact]
    public void A_closed_clients_entry_is_dropped_once_it_has_expired()
    {
        var limit = Limit();
        for (var i = 0; i < ParcelBuyLimit.PerWindow; i++) limit.TryTake(A);
        _time.Advance(ParcelBuyLimit.Window + TimeSpan.FromSeconds(1));

        limit.Forget(A);

        Assert.Equal(0, limit.Count);
        Assert.True(limit.TryTake(A));
    }

    [Fact]
    public void A_payout_limit_warns_once_a_minute_per_owner()
    {
        var limit = new PayoutLimit(_time);
        for (var i = 0; i < PayoutLimit.PerWindow; i++) limit.TryTake(A, out _);

        Assert.False(limit.TryTake(A, out var first));
        Assert.False(limit.TryTake(A, out var second));
        _time.Advance(PayoutLimit.Window + TimeSpan.FromSeconds(1));
        Assert.True(limit.TryTake(A, out _));
        for (var i = 1; i < PayoutLimit.PerWindow; i++) limit.TryTake(A, out _);
        Assert.False(limit.TryTake(A, out var third));

        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
    }

    [Fact]
    public void The_payout_table_stays_bounded()
    {
        var limit = new PayoutLimit(_time);
        for (var i = 0; i < PayoutLimit.MaxOwners * 3; i++)
        {
            limit.TryTake(Guid.NewGuid(), out _);
            if (i % 1000 == 0) _time.Advance(PayoutLimit.Window + TimeSpan.FromSeconds(1));
        }

        Assert.True(limit.Count <= PayoutLimit.MaxOwners + 1000);
    }
}
