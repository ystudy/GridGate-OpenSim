using GridGate.Region.Money;
using Nini.Config;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>When the module turns itself on (money plan 2, task 5).</summary>
public sealed class ModuleLoadTests
{
    private static IniConfigSource Config(bool economy = true, params string[] skip)
    {
        var source = new IniConfigSource();
        if (economy) source.AddConfig("Economy").Set("EconomyModule", "GridGateMoneyModule");
        var money = source.AddConfig("GridGateMoney");
        foreach (var (key, value) in new[]
                 {
                     ("ServiceURI", "https://127.0.0.1:8000/money/v1/"), ("RegionKey", "k"), ("ServiceFingerprint", new string('A', 64)),
                     ("HelperURI", "http://example.test:8001/economy/"),
                 })
            if (!skip.Contains(key)) money.Set(key, value);
        return source;
    }

    [Fact]
    public void Module_disabled_without_config()
    {
        var module = new GridGateMoneyModule();

        module.Initialise(new IniConfigSource());

        Assert.False(module.Enabled);
        module.AddRegion(null!); // disabled: never touches the scene
    }

    [Fact]
    public void Module_enabled_with_all_keys()
    {
        var module = new GridGateMoneyModule();

        module.Initialise(Config());

        Assert.True(module.Enabled);
        Assert.Equal("GridGateMoneyModule", module.Name);
        Assert.Null(module.ReplaceableInterface);
        Assert.Equal(0, module.GroupCreationCharge);
        module.Close();
    }

    [Fact]
    public void Profiles_ask_this_module_for_the_classified_fee()
    {
        var module = new GridGateMoneyModule();
        module.Initialise(Config());

        var fees = Assert.IsAssignableFrom<OpenSim.Framework.IClassifiedFeeModule>(module);

        Assert.Equal(0, fees.ClassifiedFee); // no economy answer yet
        Assert.False(fees.ClassifiedFeeCovered(OpenMetaverse.UUID.Random())); // not in any region here
        module.Close();
    }

    [Theory]
    [InlineData("ServiceURI")]
    [InlineData("RegionKey")]
    [InlineData("ServiceFingerprint")]
    [InlineData("HelperURI")]
    public void Any_missing_key_keeps_it_off(string missing)
    {
        var module = new GridGateMoneyModule();

        module.Initialise(Config(skip: missing));

        Assert.False(module.Enabled);
    }

    [Fact]
    public void Another_economy_module_keeps_it_off()
    {
        var module = new GridGateMoneyModule();

        module.Initialise(Config(economy: false));

        Assert.False(module.Enabled);
    }

    [Fact]
    public void Module_enabled_unpaired_with_helper_only()
    {
        var source = new IniConfigSource();
        source.AddConfig("Economy").Set("EconomyModule", "GridGateMoneyModule");
        var money = source.AddConfig("GridGateMoney");
        money.Set("Paired", "false");
        money.Set("HelperURI", "http://grid.example:8001/economy/");
        var module = new GridGateMoneyModule();

        module.Initialise(source);

        Assert.True(module.Enabled);
        Assert.False(module.IsPaired);
        module.Close();
    }

    [Fact]
    public void Unpaired_without_helper_stays_off()
    {
        var source = new IniConfigSource();
        source.AddConfig("Economy").Set("EconomyModule", "GridGateMoneyModule");
        source.AddConfig("GridGateMoney").Set("Paired", "false");
        var module = new GridGateMoneyModule();

        module.Initialise(source);

        Assert.False(module.Enabled);
    }

    [Fact]
    public void Paired_module_says_so()
    {
        var module = new GridGateMoneyModule();

        module.Initialise(Config());

        Assert.True(module.IsPaired);
        module.Close();
    }

    [Fact]
    public void Plain_http_service_keeps_it_off()
    {
        var source = Config();
        source.Configs["GridGateMoney"].Set("ServiceURI", "http://127.0.0.1:8000/money/v1/");
        var module = new GridGateMoneyModule();

        module.Initialise(source);

        Assert.False(module.Enabled);
    }
}
