using GridGate.Launcher.Core;
using GridGate.Launcher.Core.Config;
using GridGate.Launcher.Core.Ini;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>What the config writer puts in the INIs for Gloebit (money plan 4, task 2).</summary>
public sealed class GloebitConfigTests : IDisposable
{
    private const string Key = "0b9e3c2a-1111-4c5d-8e9f-0123456789ab";
    private const string Secret = "7d3f9a10-2222-4b6c-9d8e-fedcba987654";
    private static readonly GloebitSettings App = new("sandbox", Key, Secret, "Grid Owner", "owner@example.org");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gg-gloebit-cfg-" + Guid.NewGuid().ToString("N"));
    private readonly LauncherPaths _paths;

    public GloebitConfigTests()
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

    private static LauncherSettings Grid(MoneyMode mode, GloebitSettings? app = null) => TestSettings.Create() with
    {
        Plan = OwnerPlan.GridAsset,
        Hostname = "192.168.1.5",
        Programs = [new ProgramSettings("p1", 9000, 9001), new ProgramSettings("p2", 9002, 9003)],
        Regions =
        [
            new RegionSettings("p1", "One", Guid.NewGuid(), 1000, 1000, UdpPort: 8004),
            new RegionSettings("p2", "Two", Guid.NewGuid(), 1002, 1000, UdpPort: 8005),
        ],
        Money = new MoneySettings(Mode: mode, CurrencySymbol: "GG", ProgramKeys: new() { ["p1"] = "k1-000000000000000000000000000000000000000000000", ["p2"] = "k2-000000000000000000000000000000000000000000000" },
            Gloebit: app),
    };

    private void Write(LauncherSettings s, bool open = true) => new GridConfigWriter(_paths, open ? MoneyGate.Open : null).Write(s, []);

    private IniDocument Region(string instance) => IniDocument.Load(Path.Combine(_paths.RegionDir(instance), "OpenSim.ini"));
    private IniDocument Robust() => IniDocument.Load(Path.Combine(_paths.RuntimeOpenSim, "Robust.HG.ini"));

    [Fact]
    public void Gloebit_mode_writes_the_module_and_its_section()
    {
        Write(Grid(MoneyMode.Gloebit, App));

        foreach (var p in new[] { "p1", "p2" })
        {
            var r = Region(p);
            Assert.Equal("Gloebit", r.Get("Economy", "economymodule"));
            Assert.Equal("true", r.Get("Gloebit", "Enabled"));
            Assert.Equal("sandbox", r.Get("Gloebit", "GLBEnvironment"));
            Assert.Equal(Key, r.Get("Gloebit", "GLBKey"));
            Assert.Equal(Secret, r.Get("Gloebit", "GLBSecret"));
            Assert.Equal("Grid Owner", r.Get("Gloebit", "GLBOwnerName"));
            Assert.Equal("owner@example.org", r.Get("Gloebit", "GLBOwnerEmail"));
            Assert.DoesNotContain(r.ActiveEntries(), e => e.Section == "GridGateMoney");
        }
    }

    /// <summary>
    /// Outside Gloebit mode a region may still find Gloebit's DLL (one a running region held when the mode changed).
    /// Gloebit's module starts enabled unless a [Gloebit] section says otherwise, so every such INI says Enabled=false,
    /// and nothing more (live check, money plan 4).
    /// </summary>
    internal static void AssertGloebitOff(IniDocument r) =>
        Assert.Equal([("Enabled", "false")], r.ActiveEntries().Where(e => e.Section == "Gloebit").Select(e => (e.Key, e.Value)));

    [Fact]
    public void Gloebit_mode_names_gloebit_where_its_module_looks()
    {
        // Gloebit reads [Startup] and [Economy] "economymodule" case-sensitively; GridGate's [Economy] key is
        // "EconomyModule", so without this Gloebit only "defaults to Gloebit since dll is present" (live check).
        Write(Grid(MoneyMode.Gloebit, App));

        Assert.Contains(Region("p1").ActiveEntries(), e => e is ("Startup", "economymodule", "Gloebit"));
    }

    [Fact]
    public void Other_modes_give_a_leftover_gloebit_its_own_file_too()
    {
        // Outside Gloebit mode a leftover Gloebit.dll still runs LoadConfig first: with no file in its config directory
        // it copies its example there and stops the program (review, money plan 4). Its own file says Enabled=false.
        foreach (var (mode, open) in new[] { (MoneyMode.Play, true), (MoneyMode.Off, true), (MoneyMode.Gloebit, false) })
        {
            Write(Grid(mode, App), open);
            foreach (var p in new[] { "p1", "p2" })
            {
                var dir = Region(p).Get("Startup", "ConfigDirectory");
                Assert.Equal(Path.GetFullPath(_paths.RegionDir(p)), Path.GetFullPath(dir!));
                AssertGloebitOff(IniDocument.Load(Path.Combine(dir!, "Gloebit.ini")));
            }
        }
    }

    [Fact]
    public void Gloebit_reads_no_addon_file_of_its_own()
    {
        // Gloebit's module merges <ConfigDirectory>/Gloebit.ini over the program's settings, and makes one from its
        // example (and stops) when there is none. Each program gets its own empty file, so what GridGate wrote counts.
        Write(Grid(MoneyMode.Gloebit, App));

        foreach (var p in new[] { "p1", "p2" })
        {
            var dir = Region(p).Get("Startup", "ConfigDirectory");
            Assert.Equal(Path.GetFullPath(_paths.RegionDir(p)), Path.GetFullPath(dir!));
            var addon = IniDocument.Load(Path.Combine(dir!, "Gloebit.ini"));
            Assert.Empty(addon.ActiveEntries());
        }
    }

    [Fact]
    public void Incomplete_gloebit_refuses_payments()
    {
        Write(Grid(MoneyMode.Gloebit, App with { Secret = "" }));

        var r = Region("p1");
        Assert.Equal(GridConfigWriter.MoneyModuleName, r.Get("Economy", "EconomyModule"));
        Assert.Equal("false", r.Get("GridGateMoney", "Paired"));
        AssertGloebitOff(r);
        Assert.NotEqual("Gloebit", r.Get("Economy", "economymodule"));
    }

    [Fact]
    public void Play_and_off_write_no_gloebit()
    {
        foreach (var mode in new[] { MoneyMode.Play, MoneyMode.Off })
        {
            Write(Grid(mode, App));
            var r = Region("p1");
            AssertGloebitOff(r);
            Assert.DoesNotContain(r.ActiveEntries(), e => e.Value.Contains("Gloebit", StringComparison.OrdinalIgnoreCase) && e.Section == "Economy");
        }
    }

    [Fact]
    public void Closed_gate_writes_no_gloebit()
    {
        Write(Grid(MoneyMode.Gloebit, App), open: false);

        var r = Region("p1");
        Assert.DoesNotContain(r.ActiveEntries(), e => e.Section == "GridGateMoney");
        AssertGloebitOff(r);
        Assert.Equal("", r.Get("Economy", "EconomyModule"));
    }

    [Fact]
    public void Login_currency_follows_the_mode()
    {
        Write(Grid(MoneyMode.Play));
        Assert.Equal("GG", Robust().Get("LoginService", "Currency"));

        Write(Grid(MoneyMode.Gloebit, App));
        Assert.Equal("G$", Robust().Get("LoginService", "Currency"));

        Write(Grid(MoneyMode.Off));
        Assert.DoesNotContain(Robust().ActiveEntries(), e => e.Section == "LoginService" && e.Key == "Currency");
    }

    [Fact]
    public void Gloebit_values_with_ini_breakers_are_not_written()
    {
        Write(Grid(MoneyMode.Gloebit, App with { OwnerName = "Grid; Owner" })); // a hand-edited settings file

        var r = Region("p1");
        AssertGloebitOff(r);
        Assert.Equal("false", r.Get("GridGateMoney", "Paired"));
    }

    [Fact]
    public void Gloebit_here_needs_mode_app_and_a_plan_with_regions_of_its_own()
    {
        Assert.True(MoneyPlace.GloebitHere(Grid(MoneyMode.Gloebit, App), MoneyGate.Open));
        Assert.False(MoneyPlace.GloebitHere(Grid(MoneyMode.Gloebit), MoneyGate.Open));
        Assert.False(MoneyPlace.GloebitHere(Grid(MoneyMode.Play, App), MoneyGate.Open));
        Assert.False(MoneyPlace.GloebitHere(Grid(MoneyMode.Gloebit, App), MoneyRelease.Effective));
        Assert.False(MoneyPlace.GloebitHere(Grid(MoneyMode.Gloebit, App) with { Plan = OwnerPlan.Connected }, MoneyGate.Open));
    }
}
