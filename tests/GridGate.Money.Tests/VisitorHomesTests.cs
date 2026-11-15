using GridGate.Region.Money;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>
/// Which home a visitor's wallet is keyed by in the region (money review 2026-11-15, R-3). OpenSim's name cache can be
/// planted by an object's creator data (any UUID with any home), and a later real arrival doesn't replace it.
/// </summary>
public sealed class VisitorHomesTests
{
    private const string Real = "http://real.example:8002/";
    private const string Rogue = "http://rogue.example:8002/";

    [Fact]
    public void A_present_visitors_own_login_beats_a_planted_name_cache() =>
        Assert.Equal(Real, VisitorHomes.Choose(circuitHome: Real, rememberedHome: null, cachedHome: Rogue));

    [Fact]
    public void An_absent_visitor_keeps_the_home_their_login_brought() =>
        Assert.Equal(Real, VisitorHomes.Choose(circuitHome: null, rememberedHome: Real, cachedHome: Rogue));

    [Fact]
    public void The_login_beats_a_remembered_home_from_an_earlier_visit() =>
        Assert.Equal(Real, VisitorHomes.Choose(circuitHome: Real, rememberedHome: Rogue, cachedHome: Rogue));

    [Fact]
    public void A_visitor_this_program_never_saw_falls_back_to_the_name_cache() =>
        Assert.Equal(Real, VisitorHomes.Choose(circuitHome: null, rememberedHome: null, cachedHome: Real));

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", " ", "")]
    public void No_home_known_is_empty(string? circuit, string? remembered, string? cached) =>
        Assert.Equal("", VisitorHomes.Choose(circuit, remembered, cached));

    [Fact]
    public void A_blank_login_home_is_skipped() =>
        Assert.Equal(Real, VisitorHomes.Choose(circuitHome: " ", rememberedHome: Real, cachedHome: Rogue));
}
