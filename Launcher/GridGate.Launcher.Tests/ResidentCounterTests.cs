using System.Diagnostics;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Dashboard;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// The front page's resident counts without a database (spec 2026-11-19-front-page-design §2). The real queries are covered
/// against MariaDB in the integration tests (OnlineUsersTests).
/// </summary>
public sealed class ResidentCounterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(OwnerPlan.RegionServer)]
    [InlineData(OwnerPlan.Connected)]
    public async Task A_machine_without_staff_pages_counts_nothing(OwnerPlan plan)
    {
        var settings = TestSettings.Create() with { Plan = plan };

        var counts = await new ResidentCounter(() => settings).CountAsync(DateTimeOffset.UtcNow.AddDays(-30), Ct);

        Assert.Equal(ResidentCounts.Unknown, counts);
    }

    [Fact]
    public async Task A_database_that_is_down_gives_unknown_quickly()
    {
        // Nothing listens on this port: the connection is refused at once.
        var settings = TestSettings.Create() with { MariaDbPort = 1 };
        var watch = Stopwatch.StartNew();

        var counts = await new ResidentCounter(() => settings).CountAsync(DateTimeOffset.UtcNow.AddDays(-30), Ct);

        Assert.Equal(ResidentCounts.Unknown, counts);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }
}
