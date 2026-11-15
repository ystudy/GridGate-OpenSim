using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>What a Gloebit app's settings must be (money plan 4, task 1).</summary>
public sealed class GloebitRulesTests
{
    private const string Key = "0b9e3c2a-1111-4c5d-8e9f-0123456789ab";
    private const string Secret = "7d3f9a10-2222-4b6c-9d8e-fedcba987654";

    private static GloebitSettings Valid() => new("sandbox", Key, Secret, "Grid Owner", "owner@example.org");

    [Fact]
    public void A_valid_app_has_no_problem() => Assert.Null(GloebitRules.Problem(Valid()));

    [Theory]
    [InlineData("staging", Key, Secret, "Grid Owner", "")]
    [InlineData("sandbox", "x", Secret, "Grid Owner", "")]
    [InlineData("sandbox", Key, "not-a-guid", "Grid Owner", "")]
    [InlineData("sandbox", Key, Secret, "", "")]
    [InlineData("sandbox", Key, Secret, "Grid; Owner", "")]
    [InlineData("sandbox", Key, Secret, "Grid [Owner]", "")]
    [InlineData("sandbox", Key, Secret, "Grid \"Owner\"", "")]
    [InlineData("sandbox", Key, Secret, "Grid Owner", "a@")]
    [InlineData("sandbox", Key, Secret, "Grid Owner", "owner@example;org")]
    public void Problem_spots_bad_values(string env, string key, string secret, string name, string email) =>
        Assert.NotNull(GloebitRules.Problem(new GloebitSettings(env, key, secret, name, email)));

    [Fact]
    public void Production_is_allowed() => Assert.Null(GloebitRules.Problem(Valid() with { Environment = "production" }));

    [Fact]
    public void Email_is_optional() => Assert.Null(GloebitRules.Problem(Valid() with { OwnerEmail = "" }));

    [Fact]
    public void Complete_needs_key_secret_and_name()
    {
        Assert.True(Valid().Complete);
        Assert.False((Valid() with { Key = "" }).Complete);
        Assert.False((Valid() with { Secret = "" }).Complete);
        Assert.False((Valid() with { OwnerName = "" }).Complete);
        Assert.False((Valid() with { Environment = "elsewhere" }).Complete);
        Assert.False(new GloebitSettings().Complete);
    }

    [Fact]
    public void ToString_never_shows_the_secret()
    {
        var text = $"{Valid()} {new MoneySettings(Gloebit: Valid())}";

        Assert.DoesNotContain(Secret, text);
    }

    [Fact]
    public void Masked_keeps_the_last_four()
    {
        Assert.Equal("••••89ab", GloebitRules.Masked(Key));
        Assert.Equal("", GloebitRules.Masked(""));
    }
}
