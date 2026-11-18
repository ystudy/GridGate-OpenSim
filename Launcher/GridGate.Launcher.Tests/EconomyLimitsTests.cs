using System.Net;
using GridGate.Launcher.Core.Database;
using GridGate.Launcher.Economy;
using Microsoft.Extensions.Time.Testing;
using MySqlConnector;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>What keeps the economy helper from flooding the shared database: lookups at once, calls per second, addresses tracked.</summary>
public sealed class EconomyLimitsTests
{
    private FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

    private static IPAddress Ip(int n) => IPAddress.Parse($"203.0.113.{n}");

    [Fact]
    public void An_unknown_caller_counts_toward_the_grid_wide_second()
    {
        var l = new EconomyLimits(Clock, perSecond: 3);

        for (var i = 0; i < 3; i++) Assert.True(l.Allow(null));

        Assert.False(l.Allow(null));
        Assert.False(l.TryTakeLookupSecond());
        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(l.Allow(null));
    }

    [Fact]
    public void A_ninth_lookup_at_once_is_refused()
    {
        var l = new EconomyLimits(Clock);
        for (var i = 0; i < 8; i++) Assert.True(l.TryStartLookup());

        Assert.False(l.TryStartLookup());
        l.EndLookup();
        Assert.True(l.TryStartLookup());
    }

    [Fact]
    public void Lookups_past_twenty_in_a_second_are_refused()
    {
        var l = new EconomyLimits(Clock);
        for (var i = 1; i <= 20; i++) Assert.True(l.TryTakeLookupSecond());

        Assert.False(l.TryTakeLookupSecond());
        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(l.TryTakeLookupSecond());
    }

    [Fact]
    public void Calls_from_many_addresses_do_not_use_the_second_until_they_look_up()
    {
        var l = new EconomyLimits(Clock);
        for (var i = 1; i <= 100; i++) Assert.True(l.Allow(Ip(i)));

        for (var i = 1; i <= 20; i++) Assert.True(l.TryTakeLookupSecond());
        Assert.False(l.TryTakeLookupSecond());
    }

    private static readonly Guid Agent = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Secure = Guid.Parse("60000000-0000-0000-0000-000000000006");

    [Fact]
    public void A_checked_session_skips_the_second_and_the_lookup_cap_for_ten_minutes()
    {
        var l = new EconomyLimits(Clock);
        l.RememberChecked(Agent, Secure);
        for (var i = 1; i <= 20; i++) Assert.True(l.TryTakeLookupSecond());
        for (var i = 0; i < 8; i++) Assert.True(l.TryStartLookup());

        Clock.Advance(TimeSpan.FromMinutes(9));

        Assert.True(l.IsChecked(Agent, Secure));
        Assert.False(l.IsChecked(Agent, Guid.NewGuid()));
    }

    [Fact]
    public void A_checked_session_is_looked_up_again_after_ten_minutes()
    {
        var l = new EconomyLimits(Clock);
        l.RememberChecked(Agent, Secure);

        Clock.Advance(TimeSpan.FromMinutes(10));

        Assert.False(l.IsChecked(Agent, Secure));
    }

    [Fact]
    public void The_checked_session_cache_drops_the_oldest_past_its_capacity()
    {
        var l = new EconomyLimits(Clock, checkedCapacity: 3);
        var ids = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
        {
            l.RememberChecked(Agent, id);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(l.IsChecked(Agent, ids[0]));
        foreach (var id in ids.Skip(1)) Assert.True(l.IsChecked(Agent, id));
    }

    [Fact]
    public void A_call_refused_by_its_own_addresss_limit_takes_no_slot_in_the_second()
    {
        var l2 = new EconomyLimits(Clock, perSecond: 61);
        for (var i = 0; i < EconomyLimits.PerMinute; i++) Assert.True(l2.Allow(Ip(1)));
        for (var i = 0; i < 20; i++) Assert.False(l2.Allow(Ip(1)));

        Assert.True(l2.Allow(Ip(2)));
    }

    [Fact]
    public void A_full_table_refuses_a_new_address_but_still_counts_known_ones()
    {
        var l = new EconomyLimits(Clock, maxAddresses: 3, perSecond: 1000);
        for (var i = 1; i <= 3; i++) Assert.True(l.Allow(Ip(i)));

        Assert.False(l.Allow(Ip(4)));
        Assert.True(l.Allow(Ip(1)));
    }

    [Fact]
    public void A_full_table_takes_new_addresses_again_once_old_ones_age_out()
    {
        var l = new EconomyLimits(Clock, maxAddresses: 3, perSecond: 1000);
        for (var i = 1; i <= 3; i++) Assert.True(l.Allow(Ip(i)));
        Assert.False(l.Allow(Ip(4)));

        Clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(l.Allow(Ip(4)));
    }

    [Fact]
    public void The_helpers_robust_connections_are_pooled_and_capped()
    {
        var settings = TestSettings.Create();
        var b = new MySqlConnectionStringBuilder(MariaDbServer.ConnectionString(settings, "robust", "robustuser", "pw", 10, maxPool: 8));

        Assert.True(b.Pooling);
        Assert.Equal(8u, b.MaximumPoolSize);
        Assert.Equal(0u, b.MinimumPoolSize);
        Assert.Equal(60u, b.ConnectionIdleTimeout);
        Assert.False(new MySqlConnectionStringBuilder(MariaDbServer.ConnectionString(settings, "robust", "robustuser", "pw", 10)).Pooling);
    }
}
