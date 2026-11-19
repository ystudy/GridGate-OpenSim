using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class MoneySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg-money-settings-" + Guid.NewGuid().ToString("N"));

    public MoneySettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var m = new MoneySettings();
        Assert.Equal(MoneyMode.Off, m.Mode);
        Assert.Equal("C$", m.CurrencySymbol);
        Assert.Equal("coins", m.CurrencyName);
        Assert.Equal(100, m.StartingBalancePlay);
        Assert.Equal(0, m.StartingBalanceReal);
        Assert.Equal(0, m.WeeklyAllowance);
        Assert.Equal(0, m.UploadFee);
        Assert.Equal(0, m.ClassifiedFee);
        Assert.Equal(5000, m.DailyPayoutLimit);
        Assert.Equal(1_000_000, m.MaxPayment);
        Assert.True(m.ScriptPayouts);
        Assert.True(m.VisitorWallets);
        Assert.Empty(m.Keys);
    }

    [Theory]
    [InlineData(MoneyMode.Play)]
    [InlineData(MoneyMode.Gloebit)]
    public void Play_coins_and_Gloebit_are_approved(MoneyMode mode) =>
        Assert.Equal(mode, MoneyRelease.Effective(new MoneySettings(Mode: mode)));

    [Fact]
    public void Real_coins_wait_for_their_own_approval() =>
        Assert.Equal(MoneyMode.Off, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Real)));

    [Theory]
    [InlineData(MoneyMode.Play)]
    [InlineData(MoneyMode.Real)]
    [InlineData(MoneyMode.Gloebit)]
    public void A_closed_gate_reads_every_mode_as_off(MoneyMode mode) =>
        Assert.Equal(MoneyMode.Off, MoneyGate.Closed(new MoneySettings(Mode: mode)));

    [Fact]
    public void Approvals_open_the_gate_one_level_at_a_time()
    {
        Assert.Equal(MoneyMode.Play, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Play), true, false));
        Assert.Equal(MoneyMode.Gloebit, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Gloebit), true, false));
        Assert.Equal(MoneyMode.Off, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Real), true, false));
        Assert.Equal(MoneyMode.Off, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Real), false, true));
        Assert.Equal(MoneyMode.Real, MoneyRelease.Effective(new MoneySettings(Mode: MoneyMode.Real), true, true));
    }

    [Fact]
    public void Starting_balance_follows_the_mode()
    {
        var m = new MoneySettings(StartingBalancePlay: 100, StartingBalanceReal: 7);
        Assert.Equal(100, m.StartingBalance(MoneyMode.Play));
        Assert.Equal(7, m.StartingBalance(MoneyMode.Real));
        Assert.Equal(0, m.StartingBalance(MoneyMode.Off));
        Assert.Equal(0, m.StartingBalance(MoneyMode.Gloebit));
    }

    [Fact]
    public void Schema_6_file_without_money_loads_with_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 6,
              "db": { "rootPassword": "r", "robustPassword": "b", "opensimPassword": "o" },
              "console": { "user": "u", "password": "p" },
              "machineId": "m"
            }
            """);

        var s = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(7, s.SchemaVersion);
        Assert.Equal("", s.Db.MoneyPassword);
        Assert.Equal(MoneyMode.Off, s.Money.Mode);
        Assert.Equal(8000, s.MoneyPort);
    }

    [Fact]
    public void Money_settings_round_trip()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        var s = store.LoadOrCreate() with
        {
            Money = new MoneySettings(Mode: MoneyMode.Play, CurrencySymbol: "Z$", ProgramKeys: new() { ["a"] = "k" }),
        };
        store.Save(s);

        var back = new SettingsStore(path).LoadOrCreate();

        Assert.Equal(MoneyMode.Play, back.Money.Mode);
        Assert.Equal("Z$", back.Money.CurrencySymbol);
        Assert.Equal("k", back.Money.Keys["a"]);
    }

    private MoneySettings LoadMoney(string moneyJson)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 7,
              "db": { "rootPassword": "r", "robustPassword": "b", "opensimPassword": "o" },
              "console": { "user": "u", "password": "p" },
              "machineId": "m",
              "money": {{moneyJson}}
            }
            """);
        return new SettingsStore(path).LoadOrCreate().Money;
    }

    [Fact]
    public void Old_settings_with_VisitorWallets_true_read_as_Listed()
    {
        var m = LoadMoney("""{ "mode": "Play", "visitorWallets": true }""");

        Assert.Null(m.Visitors);
        Assert.Equal(VisitorMoney.Listed, m.EffectiveVisitors);
    }

    [Fact]
    public void Old_settings_with_VisitorWallets_false_read_as_Off()
    {
        var m = LoadMoney("""{ "mode": "Play", "visitorWallets": false }""");

        Assert.Equal(VisitorMoney.Off, m.EffectiveVisitors);
    }

    [Fact]
    public void Visitors_is_saved_by_name_and_wins_over_VisitorWallets()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        store.Save(store.LoadOrCreate() with
        {
            Money = new MoneySettings(VisitorWallets: false, Visitors: VisitorMoney.All, ListedGrids: ["http://a.example:8002"]),
        });

        Assert.Contains("\"All\"", File.ReadAllText(path));
        var back = new SettingsStore(path).LoadOrCreate().Money;
        Assert.Equal(VisitorMoney.All, back.EffectiveVisitors);
        Assert.Equal(["http://a.example:8002"], back.ListedGrids);
    }

    [Fact]
    public void New_settings_have_the_limit_defaults()
    {
        var m = new MoneySettings();

        Assert.Equal(10_000, m.DailySpendLimit);
        Assert.Equal(100_000, m.DailyKeySpendLimit);
        Assert.Equal(20_000, m.DailyKeyPayoutLimit);
        Assert.Equal(VisitorMoney.Listed, m.EffectiveVisitors);
        Assert.False(m.GridListed("http://any.example:8002"));
    }

    [Fact]
    public void Old_settings_read_with_the_limit_defaults()
    {
        var m = LoadMoney("""{ "mode": "Play" }""");

        Assert.Equal(10_000, m.DailySpendLimit);
        Assert.Equal(100_000, m.DailyKeySpendLimit);
        Assert.Equal(20_000, m.DailyKeyPayoutLimit);
    }

    [Fact]
    public void GridListed_matches_exactly_after_normalising()
    {
        var m = new MoneySettings(ListedGrids: ["http://Other.Example:8002/", "http://third.example"]);

        Assert.True(m.GridListed("http://other.example:8002"));
        Assert.True(m.GridListed("HTTP://OTHER.EXAMPLE:8002//"));
        Assert.True(m.GridListed("http://third.example/"));
        Assert.False(m.GridListed("http://other.example:8003"));
        Assert.False(m.GridListed("http://other.example"));
        Assert.False(m.GridListed(""));
        // MoneyHomes.Normal turns an accented domain into punycode, so a look-alike is a different grid.
        Assert.False(m.GridListed("http://othér.example:8002"));
        Assert.False(new MoneySettings(ListedGrids: ["http://othér.example:8002"]).GridListed("http://other.example:8002"));
        Assert.True(new MoneySettings(ListedGrids: ["http://othér.example:8002"]).GridListed("http://OTHÉR.example:8002/"));
    }

    [Fact]
    public void Negative_limits_cant_be_saved()
    {
        var offered = new[] { MoneyMode.Off, MoneyMode.Play };

        Assert.Null(GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(), offered));
        Assert.Contains("daily spending limit", GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(DailySpendLimit: -1), offered));
        Assert.Contains("region spending limit", GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(DailyKeySpendLimit: -1), offered));
        Assert.Contains("region payout limit", GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(DailyKeyPayoutLimit: -1), offered));
        Assert.Null(GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(DailySpendLimit: 0), offered));
    }

    [Fact]
    public void Limits_above_100_million_cant_be_saved()
    {
        var offered = new[] { MoneyMode.Off, MoneyMode.Play };
        const long max = GridGate.Launcher.Money.MoneyPageRules.MaxDailyPayoutLimit;

        Assert.Equal(100_000_000, max);
        Assert.Contains("daily spending limit must be 0 to 100,000,000",
            GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(DailySpendLimit: max + 1), offered));
        Assert.Contains("daily region spending limit must be 0 to 100,000,000",
            GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(DailyKeySpendLimit: max + 1), offered));
        Assert.Contains("daily region payout limit must be 0 to 100,000,000",
            GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(DailyKeyPayoutLimit: max + 1), offered));
        Assert.Contains("region spending limit", GridGate.Launcher.Money.MoneyPageRules.Problem(new MoneySettings(DailyKeySpendLimit: long.MaxValue), offered));
        Assert.Null(GridGate.Launcher.Money.MoneyPageRules.Problem(
            new MoneySettings(DailySpendLimit: max, DailyKeySpendLimit: max, DailyKeyPayoutLimit: max), offered));
    }

    [Fact]
    public void Money_port_is_taken() => Assert.Contains(8000, PortAllocator.Taken(TestSettings.Create()));
}
