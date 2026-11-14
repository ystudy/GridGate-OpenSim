using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Ini;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>What the config writer puts in the INIs for money (money plan 2, task 2).</summary>
public sealed class MoneyConfigTests : IDisposable
{
    private const string KeyOne = "key-for-program-one-000000000000000000000000000";
    private const string KeyTwo = "key-for-program-two-000000000000000000000000000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-money-cfg-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public MoneyConfigTests()
    {
        _paths = new LauncherPaths(Path.Combine(_root, "data"), Path.Combine(_root, "program"), null);
        var bin = RepoBin();
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

    private static string RepoBin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridGate.sln"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("GridGate.sln not found"), "bin");
    }

    private static LauncherSettings Make(string symbol = "C$", int moneyPort = 8000) => TestSettings.Create() with
    {
        Hostname = "192.168.1.50",
        WebPort = 8080,
        MoneyPort = moneyPort,
        Programs = [new ProgramSettings("p1", 9000, 9001), new ProgramSettings("p2", 9002, 9003)],
        Regions =
        [
            new RegionSettings("p1", "One", Guid.Parse("11111111-0000-0000-0000-000000000001"), 1000, 1000, UdpPort: 8004),
            new RegionSettings("p2", "Two", Guid.Parse("11111111-0000-0000-0000-000000000002"), 1002, 1000, UdpPort: 8005),
        ],
        Money = new MoneySettings(Mode: MoneyMode.Play, CurrencySymbol: symbol, ProgramKeys: new() { ["p1"] = KeyOne, ["p2"] = KeyTwo }),
    };

    private void Write(LauncherSettings s, bool open = true) =>
        new GridConfigWriter(_paths, open ? MoneyGate.Open : null).Write(s, []);

    private IniDocument Region(string instance) => IniDocument.Load(Path.Combine(_paths.RegionDir(instance), "OpenSim.ini"));
    private IniDocument Robust() => IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini"));
    private IniDocument Grid() => IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "config-include", "GridCommon.ini"));

    private const string Helper = "http://192.168.1.50:8080/economy/";

    [Fact]
    public void Off_writes_no_money_keys()
    {
        Write(Make(), open: false);

        var r = Region("p1");
        Assert.DoesNotContain(r.ActiveEntries(), e => e.Section == "GridGateMoney");
        Assert.Equal("", r.Get("Economy", "EconomyModule"));
    }

    [Fact]
    public void On_writes_each_programs_own_key()
    {
        Write(Make());

        Assert.Equal(KeyOne, Region("p1").Get("GridGateMoney", "RegionKey"));
        Assert.Equal(KeyTwo, Region("p2").Get("GridGateMoney", "RegionKey"));
    }

    [Fact]
    public void A_program_without_a_key_stays_off()
    {
        Write(Make() with { Money = Make().Money with { ProgramKeys = new() { ["p1"] = KeyOne } } });

        Assert.Equal("", Region("p2").Get("Economy", "EconomyModule"));
        Assert.DoesNotContain(Region("p2").ActiveEntries(), e => e.Section == "GridGateMoney");
    }

    [Fact]
    public void On_names_the_module()
    {
        Write(Make());

        var r = Region("p1");
        Assert.Equal("GridGateMoneyModule", r.Get("Economy", "EconomyModule"));
        Assert.Equal("", r.Get("Startup", "economymodule"));
    }

    [Fact]
    public void Service_uri_uses_the_money_port()
    {
        Write(Make(moneyPort: 8123));

        Assert.Equal("https://127.0.0.1:8123/money/v1/", Region("p1").Get("GridGateMoney", "ServiceURI"));
    }

    [Fact]
    public void Fingerprint_matches_the_certificate()
    {
        Write(Make());

        using var cert = MoneyCertificate.LoadOrCreate(Path.Combine(_paths.Security, MoneyCertificate.FileName), TimeProvider.System);
        Assert.Equal(MoneyCertificate.Fingerprint(cert), Region("p1").Get("GridGateMoney", "ServiceFingerprint"));
    }

    [Fact]
    public void Helper_uri_ends_with_a_slash()
    {
        Write(Make());

        Assert.Equal(Helper, WebsiteAddress.Economy(Make()));
        Assert.Equal(Helper, Robust().Get("GridInfoService", "economy"));
        Assert.Equal(Helper, Grid().Get("GridInfo", "economy"));
        Assert.Equal(Helper, Region("p1").Get("GridGateMoney", "HelperURI"));
    }

    [Fact]
    public void Helper_uri_is_written_when_off_too()
    {
        Write(Make(), open: false);

        Assert.Equal(Helper, Robust().Get("GridInfoService", "economy"));
        Assert.Equal(Helper, Grid().Get("GridInfo", "economy"));
    }

    [Fact]
    public void Login_currency_is_the_symbol_when_on()
    {
        Write(Make(symbol: "GG"));
        Assert.Equal("GG", Robust().Get("LoginService", "Currency"));

        Write(Make(symbol: "GG"), open: false);
        Assert.Equal("G$", Robust().Get("LoginService", "Currency"));
    }

    [Fact]
    public void A_server_joined_to_another_grid_writes_no_money()
    {
        // Its grid's money service and helper aren't on this computer: pairing is money plan 3.
        Write(Make() with { Plan = OwnerPlan.RegionServer });

        Assert.Equal("", Region("p1").Get("Economy", "EconomyModule"));
        Assert.DoesNotContain(Region("p1").ActiveEntries(), e => e.Section == "GridGateMoney");
        Assert.NotEqual(Helper, Grid().Get("GridInfo", "economy"));
    }
}
