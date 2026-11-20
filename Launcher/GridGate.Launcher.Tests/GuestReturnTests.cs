using GridGate.Launcher.Guests;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Where sign-in goes afterwards (spec 2026-11-20-public-map-design, "Coming back after sign-in"): this site only.</summary>
public sealed class GuestReturnTests
{
    [Theory]
    [InlineData("/map")]
    [InlineData("/guest/inventory?x=1")]
    public void Pick_keeps_local_paths(string path) => Assert.Equal(path, GuestReturn.Pick(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("map")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example/")]
    [InlineData("/\r\nx")]
    public void Pick_refuses_other_places(string? returnUrl) => Assert.Equal("/guest", GuestReturn.Pick(returnUrl));
}
