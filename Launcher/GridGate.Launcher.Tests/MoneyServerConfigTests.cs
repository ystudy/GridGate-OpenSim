using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Ini;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>
/// What the config writer puts in the INIs for Region Servers' money (money plan 3, tasks 5 and 6): the grid info a Grid
/// Asset Server publishes, and a Region Server's paired, unpaired and Off sections.
/// </summary>
public sealed class MoneyServerConfigTests : IDisposable
{
    private const string Key = "the-paired-servers-key-0000000000000000000000000";
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private const string Economy = "http://192.168.1.5:8001/economy/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-money-scfg-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public MoneyServerConfigTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        var bin = Path.Combine(dir!.FullName, "bin");
        var rt = _paths.RuntimeOpenSim;
        Directory.CreateDirectory(Path.Combine(rt, "config-include", "Proto", "Robust"));
        File.Copy(Path.Combine(bin, "Robust.HG.ini.proto"), Path.Combine(rt, "Robust.HG.ini.proto"));
        File.Copy(Path.Combine(bin, "config-include", "FlotsamCache.ini.example"), Path.Combine(rt, "config-include", "FlotsamCache.ini.example"));
        foreach (var f in new[] { "GridCommon.ini", "OpenSim.ini" })
            File.Copy(Path.Combine(bin, "config-include", "Proto", "Robust", f), Path.Combine(rt, "config-include", "Proto", "Robust", f));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static readonly JoinedGrid Joined = new("192.168.1.5", 8002, 8003, "Avalon", "http://192.168.1.5:8002/", "1.0", false, 8004, 8006,
        Money: MoneyMode.Play, MoneyPort: 8000, MoneyFingerprint: Fingerprint, Economy: Economy);

    private static LauncherSettings RegionServer(JoinedGrid? join = null, ServerPairing? pairing = null) => TestSettings.Create() with
    {
        Plan = OwnerPlan.RegionServer,
        Hostname = "192.168.1.20",
        Join = join ?? Joined,
        Setup = TestSettings.Create().Setup with { Owner = new OwnerInfo("Ann", "Lee", Guid.NewGuid()), OwnerCreated = true },
        Programs = [new ProgramSettings("p1", 9000, 9001), new ProgramSettings("p2", 9002, 9003)],
        Regions =
        [
            new RegionSettings("p1", "One", Guid.Parse("11111111-0000-0000-0000-000000000001"), 1000, 1000, UdpPort: 8004),
            new RegionSettings("p2", "Two", Guid.Parse("11111111-0000-0000-0000-000000000002"), 1002, 1000, UdpPort: 8005),
        ],
        Money = new MoneySettings(Pairing: pairing),
    };

    private static ServerPairing Paired(string fingerprint = Fingerprint, string host = "192.168.1.5") =>
        new(host, fingerprint, Key, DateTimeOffset.UtcNow);

    private void Write(LauncherSettings s, bool open = true) => new GridConfigWriter(_paths, open ? MoneyGate.Open : MoneyGate.Closed).Write(s, []);

    private IniDocument Region(string instance) => IniDocument.Load(Path.Combine(_paths.RegionDir(instance), "OpenSim.ini"));
    private IniDocument Robust() => IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini"));
    private IniDocument Grid() => IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "config-include", "GridCommon.ini"));

    private static LauncherSettings GridAsset(MoneyMode mode) => TestSettings.Create() with
    {
        Plan = OwnerPlan.GridAsset,
        Hostname = "192.168.1.5",
        MoneyPort = 8123,
        Programs = [new ProgramSettings("p1", 9000, 9001)],
        Regions = [new RegionSettings("p1", "One", Guid.NewGuid(), 1000, 1000, UdpPort: 8004)],
        Money = new MoneySettings(Mode: mode, ProgramKeys: new() { ["p1"] = Key }),
    };

    [Fact]
    public void Grid_info_publishes_money_when_on()
    {
        Write(GridAsset(MoneyMode.Play));

        var r = Robust();
        Assert.Equal("play", r.Get("GridInfoService", "gridgate_money"));
        Assert.Equal("8123", r.Get("GridInfoService", "gridgate_money_port"));
        using var cert = MoneyCertificate.LoadOrCreate(Path.Combine(_paths.Security, MoneyCertificate.FileName), TimeProvider.System);
        Assert.Equal(MoneyCertificate.Fingerprint(cert), r.Get("GridInfoService", "gridgate_money_fingerprint"));
    }

    [Fact]
    public void Grid_info_says_off_and_nothing_else_when_off()
    {
        Write(GridAsset(MoneyMode.Play), open: false);

        var r = Robust();
        Assert.Equal("off", r.Get("GridInfoService", "gridgate_money"));
        Assert.DoesNotContain(r.ActiveEntries(), e => e.Key is "gridgate_money_port" or "gridgate_money_fingerprint");
        Assert.False(File.Exists(Path.Combine(_paths.Security, MoneyCertificate.FileName)));
    }

    [Fact]
    public void Paired_region_server_writes_the_grids_service()
    {
        Write(RegionServer(pairing: Paired()));

        foreach (var p in new[] { "p1", "p2" })
        {
            var r = Region(p);
            Assert.Equal(GridConfigWriter.MoneyModuleName, r.Get("Economy", "EconomyModule"));
            Assert.Equal("https://192.168.1.5:8000/money/v1/", r.Get("GridGateMoney", "ServiceURI"));
            Assert.Equal(Key, r.Get("GridGateMoney", "RegionKey"));
            Assert.Equal(Fingerprint, r.Get("GridGateMoney", "ServiceFingerprint"));
            Assert.Equal(Economy, r.Get("GridGateMoney", "HelperURI"));
            Assert.DoesNotContain(r.ActiveEntries(), e => e.Key == "Paired");
        }
    }

    [Fact]
    public void Unpaired_region_server_refuses_money()
    {
        Write(RegionServer());

        var r = Region("p1");
        Assert.Equal(GridConfigWriter.MoneyModuleName, r.Get("Economy", "EconomyModule"));
        Assert.Equal("false", r.Get("GridGateMoney", "Paired"));
        Assert.Equal(Economy, r.Get("GridGateMoney", "HelperURI"));
        Assert.DoesNotContain(r.ActiveEntries(), e => e.Key is "RegionKey" or "ServiceURI" or "ServiceFingerprint");
    }

    [Fact]
    public void Changed_certificate_counts_as_unpaired()
    {
        Write(RegionServer(pairing: Paired(fingerprint: new string('0', 64))));

        Assert.Equal("false", Region("p1").Get("GridGateMoney", "Paired"));
        Assert.DoesNotContain(Region("p1").ActiveEntries(), e => e.Key == "RegionKey");
    }

    [Fact]
    public void Pairing_with_another_grid_counts_as_unpaired()
    {
        Write(RegionServer(pairing: Paired(host: "192.168.1.99")));

        Assert.Equal("false", Region("p1").Get("GridGateMoney", "Paired"));
    }

    [Fact]
    public void Region_server_with_grid_money_off_writes_nothing()
    {
        Write(RegionServer(join: Joined with { Money = MoneyMode.Off }, pairing: Paired()));

        Assert.Equal("", Region("p1").Get("Economy", "EconomyModule"));
        Assert.DoesNotContain(Region("p1").ActiveEntries(), e => e.Section == "GridGateMoney");
    }

    [Fact]
    public void Closed_gate_writes_nothing_on_a_region_server()
    {
        Write(RegionServer(pairing: Paired()), open: false);

        Assert.Equal("", Region("p1").Get("Economy", "EconomyModule"));
        Assert.DoesNotContain(Region("p1").ActiveEntries(), e => e.Section == "GridGateMoney");
    }

    [Fact]
    public void Region_server_advertises_the_grids_helper()
    {
        Write(RegionServer(join: Joined with { Money = MoneyMode.Off }), open: false);

        Assert.Equal(Economy, Grid().Get("GridInfo", "economy"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Region_server_never_writes_a_saved_helper_the_ini_reader_would_expand(bool open)
    {
        // Settings an older GridGate saved before the join read refused such an address (money review 2026-11-15, H-1).
        Write(RegionServer(join: Joined with { Economy = "http://attacker.example/${Network|ConsolePass}/" }, pairing: Paired()), open);

        Assert.Null(Grid().Get("GridInfo", "economy"));
        foreach (var p in new[] { "p1", "p2" })
            Assert.DoesNotContain("attacker", Region(p).Get("GridGateMoney", "HelperURI") ?? "");
    }

    private static readonly GloebitSettings GApp =new("sandbox", "0b9e3c2a-1111-4c5d-8e9f-0123456789ab", "7d3f9a10-2222-4b6c-9d8e-fedcba987654", "RS Owner");

    [Fact]
    public void Region_server_without_its_own_app_refuses()
    {
        Write(RegionServer(join: Joined with { Money = MoneyMode.Gloebit }));

        var r = Region("p1");
        Assert.Equal("false", r.Get("GridGateMoney", "Paired"));
        GloebitConfigTests.AssertGloebitOff(r);
    }

    [Fact]
    public void Region_server_with_its_app_runs_gloebit()
    {
        var s = RegionServer(join: Joined with { Money = MoneyMode.Gloebit });
        Write(s with { Money = s.Money with { Gloebit = GApp } });

        var r = Region("p1");
        Assert.Equal("Gloebit", r.Get("Economy", "economymodule"));
        Assert.Equal(GApp.Key, r.Get("Gloebit", "GLBKey"));
        Assert.DoesNotContain(r.ActiveEntries(), e => e.Section == "GridGateMoney");
    }

    [Fact]
    public void Connected_server_never_runs_gloebit()
    {
        var s = RegionServer() with
        {
            Plan = OwnerPlan.Connected,
            Join = null,
            Connected = new ConnectedGrid(null, "Other", "http://login.other.example:8002/", "http://login.other.example:8002/", "http://services.other.example:8003/", null),
            Money = new MoneySettings(Mode: MoneyMode.Gloebit, Gloebit: GApp),
        };

        Write(s);

        Assert.DoesNotContain(Region("p1").ActiveEntries(), e => e.Section == "GridGateMoney");
        GloebitConfigTests.AssertGloebitOff(Region("p1"));
    }

    [Fact]
    public void Connected_server_writes_no_money()
    {
        var s = RegionServer() with
        {
            Plan = OwnerPlan.Connected,
            Join = null,
            Connected = new ConnectedGrid(null, "Other", "http://login.other.example:8002/", "http://login.other.example:8002/", "http://services.other.example:8003/", null),
            Money = new MoneySettings(Mode: MoneyMode.Play, ProgramKeys: new() { ["p1"] = Key }),
        };

        Write(s);

        Assert.Equal("", Region("p1").Get("Economy", "EconomyModule"));
        Assert.DoesNotContain(Region("p1").ActiveEntries(), e => e.Section == "GridGateMoney");
    }

    [Fact]
    public void Bad_symbol_never_reaches_the_ini()
    {
        Write(GridAsset(MoneyMode.Play) with { Money = GridAsset(MoneyMode.Play).Money with { CurrencySymbol = "a=b" } });

        Assert.Equal("C$", Robust().Get("LoginService", "Currency"));
    }
}
