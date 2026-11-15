namespace GridGate.Region.Money;

/// <summary>
/// Which home grid address a hypergrid visitor's wallet is keyed by (money review 2026-11-15, R-3). OpenSim's name cache
/// (<c>IUserManagement.GetUserHomeURL</c>) can be planted by any object's creator data, a UUID with any home address, and
/// a visitor's real arrival doesn't replace it. So the cache comes last. First is the home in the visitor's own login
/// (their circuit), while they are here. Next is the one this program remembered from their last arrival.
/// </summary>
public static class VisitorHomes
{
    public static string Choose(string? circuitHome, string? rememberedHome, string? cachedHome) =>
        Known(circuitHome) ?? Known(rememberedHome) ?? Known(cachedHome) ?? "";

    private static string? Known(string? home) => string.IsNullOrWhiteSpace(home) ? null : home;
}
