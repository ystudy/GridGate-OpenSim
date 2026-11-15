using System.Net;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>Where money runs (money plan 3, task 4): the ledger only where Robust runs; a Region Server follows its grid.</summary>
public sealed class MoneyPlaceTests
{
    private static readonly JoinedGrid Joined = new("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "1.0", false, 8004, 8006);

    private static LauncherSettings On(OwnerPlan plan, MoneyMode chosen = MoneyMode.Play, MoneyMode? grid = null) =>
        TestSettings.Create() with { Plan = plan, Money = new MoneySettings(Mode: chosen), Join = Joined with { Money = grid } };

    [Fact]
    public void Region_server_never_runs_the_ledger()
    {
        var s = On(OwnerPlan.RegionServer, grid: MoneyMode.Play);

        Assert.Equal(MoneyMode.Play, MoneyPlace.GridMode(s, MoneyGate.Open));
        Assert.False(MoneyPlace.ServiceHere(s, MoneyGate.Open));
    }

    [Fact]
    public void Region_server_follows_its_grid()
    {
        Assert.Equal(MoneyMode.Off, MoneyPlace.GridMode(On(OwnerPlan.RegionServer, chosen: MoneyMode.Play, grid: MoneyMode.Off), MoneyGate.Open));
        Assert.Equal(MoneyMode.Off, MoneyPlace.GridMode(On(OwnerPlan.RegionServer, chosen: MoneyMode.Play, grid: null), MoneyGate.Open));
        Assert.Equal(MoneyMode.Play, MoneyPlace.GridMode(On(OwnerPlan.RegionServer, chosen: MoneyMode.Off, grid: MoneyMode.Play), MoneyGate.Open));
        Assert.Equal(MoneyMode.Off, MoneyPlace.GridMode(On(OwnerPlan.RegionServer, grid: MoneyMode.Play) with { Join = null }, MoneyGate.Open));
    }

    [Fact]
    public void Connected_server_has_no_money()
    {
        var s = On(OwnerPlan.Connected);

        Assert.Equal(MoneyMode.Off, MoneyPlace.GridMode(s, MoneyGate.Open));
        Assert.False(MoneyPlace.ServiceHere(s, MoneyGate.Open));
    }

    [Fact]
    public void Closed_gate_is_off_everywhere()
    {
        foreach (var plan in Enum.GetValues<OwnerPlan>())
        {
            var s = On(plan, grid: MoneyMode.Play);
            Assert.Equal(MoneyMode.Off, MoneyPlace.GridMode(s, MoneyRelease.Effective));
            Assert.False(MoneyPlace.ServiceHere(s, MoneyRelease.Effective));
        }
    }

    [Fact]
    public void Grid_asset_runs_the_ledger_when_on()
    {
        Assert.True(MoneyPlace.ServiceHere(On(OwnerPlan.GridAsset), MoneyGate.Open));
        Assert.True(MoneyPlace.ServiceHere(On(OwnerPlan.Local), MoneyGate.Open));
        Assert.False(MoneyPlace.ServiceHere(On(OwnerPlan.GridAsset, chosen: MoneyMode.Gloebit), MoneyGate.Open));
        Assert.False(MoneyPlace.ServiceHere(On(OwnerPlan.GridAsset, chosen: MoneyMode.Off), MoneyGate.Open));
    }

    [Fact]
    public void Gate_reports_where_the_service_runs()
    {
        var rs = On(OwnerPlan.RegionServer, grid: MoneyMode.Play);
        var gate = new MoneyGate(() => rs, MoneyGate.Open);

        Assert.True(gate.On);
        Assert.False(gate.ServiceHere);
        Assert.Equal(MoneyMode.Off, gate.ServiceMode());
        Assert.Equal(MoneyMode.Play, new MoneyGate(() => On(OwnerPlan.Local), MoneyGate.Open).ServiceMode());
    }

    [Fact]
    public void Dropped_addresses_are_logged_once_per_window()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var failures = new MoneyAuthFailures(time, Microsoft.Extensions.Logging.Abstractions.NullLogger<MoneyAuthFailures>.Instance);
        var ip = IPAddress.Parse("203.0.113.5");

        Assert.True(failures.FirstDropInWindow(ip));
        Assert.False(failures.FirstDropInWindow(ip));
        time.Advance(MoneyAuthFailures.Window + TimeSpan.FromSeconds(1));
        Assert.True(failures.FirstDropInWindow(ip));
    }

    [Fact]
    public void Old_tallies_are_pruned()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var failures = new MoneyAuthFailures(time, Microsoft.Extensions.Logging.Abstractions.NullLogger<MoneyAuthFailures>.Instance);
        for (var i = 0; i < MoneyAuthFailures.MaxTallies; i++)
            failures.RecordFailure(new IPAddress(BitConverter.GetBytes(0x0A000000 + i)));
        time.Advance(MoneyAuthFailures.Window + TimeSpan.FromSeconds(1));

        failures.RecordFailure(IPAddress.Parse("203.0.113.9"));

        Assert.True(failures.Tracked <= 1, $"{failures.Tracked} tallies kept");
    }

    // Minor 5: the middleware itself, with a fake connection.
    private sealed class FakeConnection(IPAddress remote) : Microsoft.AspNetCore.Connections.ConnectionContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId { get; set; } = "c1";
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
        public override System.IO.Pipelines.IDuplexPipe Transport { get; set; } = null!;
        public override EndPoint? RemoteEndPoint { get; set; } = new IPEndPoint(remote, 50000);
        public override void Abort(Microsoft.AspNetCore.Connections.ConnectionAbortedException abortReason) => Aborted = true;
    }

    [Theory]
    [InlineData("203.0.113.5", false)]
    [InlineData("192.168.1.9", true)]
    public async Task Middleware_drops_outsiders_before_anything_else(string address, bool passes)
    {
        var failures = new MoneyAuthFailures(TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<MoneyAuthFailures>.Instance);
        var reached = false;
        var app = MoneyNetworkFilter.Middleware(failures, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });
        var connection = new FakeConnection(IPAddress.Parse(address));

        await app(connection);

        Assert.Equal(passes, reached);
        Assert.Equal(!passes, connection.Aborted);
        Assert.Equal(passes ? 0 : 1, failures.Tracked);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.9", true)]
    [InlineData("10.0.0.4", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("169.254.3.3", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::5", true)]
    [InlineData("::ffff:192.168.1.9", true)]
    [InlineData("100.64.1.1", false)] // shared ISP space: other customers, not this network
    [InlineData("203.0.113.5", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:203.0.113.5", false)]
    public void Public_address_is_dropped(string address, bool allowed) =>
        Assert.Equal(allowed, MoneyNetworkFilter.Allowed(IPAddress.Parse(address)));
}
