using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// How a visitor's home grid address is stored and compared (money review 2026-11-15, L-2): in ASCII, so a look-alike
/// international domain is a different home, and the database compares it byte for byte.
/// </summary>
public sealed class MoneyHomesTests
{
    [Theory]
    [InlineData("http://Grid.Example:8002/", "http://grid.example:8002")]
    [InlineData("  http://grid.example:8002//  ", "http://grid.example:8002")]
    [InlineData("https://hg.osgrid.org", "https://hg.osgrid.org")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void An_ascii_home_is_trimmed_and_lower_case(string? home, string expected) =>
        Assert.Equal(expected, MoneyHomes.Normal(home));

    [Fact]
    public void An_international_domain_is_stored_as_punycode()
    {
        var normal = MoneyHomes.Normal("http://Othér.Example:8002/");

        Assert.Equal("http://xn--othr-dpa.example:8002", normal);
        Assert.Equal(normal, MoneyHomes.Normal("http://othér.example:8002"));
    }

    [Theory]
    [InlineData("http://othér.example:8002")]
    [InlineData("http://ôther.example:8002")]
    public void A_look_alike_is_never_the_ascii_home(string lookAlike)
    {
        var normal = MoneyHomes.Normal(lookAlike);

        Assert.NotNull(normal);
        Assert.True(normal.All(char.IsAscii), normal);
        Assert.NotEqual(MoneyHomes.Normal("http://other.example:8002"), normal);
    }

    [Theory]
    [InlineData("grid ünknown")]
    [InlineData("http://grid.example:8002/pfäd")]
    [InlineData("ftp://othér.example/")]
    public void A_home_that_cant_be_written_in_ascii_is_null(string home) => Assert.Null(MoneyHomes.Normal(home));

    [Fact]
    public void The_ledger_refuses_a_home_that_cant_be_written_in_ascii() =>
        Assert.StartsWith("system:", MoneyLedger.NormalHome("grid ünknown"));
}
