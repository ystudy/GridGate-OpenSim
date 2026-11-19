using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The one place code reads the effective money mode (money plan 2, task 1).</summary>
public sealed class MoneyGateTests
{
    private static readonly LauncherSettings PlayChosen = TestSettings.Create() with { Money = new MoneySettings(Mode: MoneyMode.Play) };

    [Fact]
    public void Closed_gate_reads_off()
    {
        var gate = new MoneyGate(() => PlayChosen, MoneyGate.Closed);

        Assert.Equal(MoneyMode.Off, gate.Effective());
        Assert.False(gate.On);
    }

    [Fact]
    public void Released_gate_runs_play_coins_and_Gloebit_but_not_real_coins()
    {
        var gate = new MoneyGate(() => PlayChosen);

        Assert.Equal(MoneyMode.Play, gate.Effective());
        Assert.True(gate.Allows(MoneyMode.Play));
        Assert.True(gate.Allows(MoneyMode.Gloebit));
        Assert.False(gate.Allows(MoneyMode.Real));
    }

    [Fact]
    public void Open_gate_passes_the_mode()
    {
        var gate = new MoneyGate(() => PlayChosen, MoneyGate.Open);

        Assert.Equal(MoneyMode.Play, gate.Effective());
        Assert.True(gate.On);
    }

    [Fact]
    public void Allows_reads_each_mode_through_the_gate()
    {
        var closed = new MoneyGate(() => PlayChosen, MoneyGate.Closed);
        var open = new MoneyGate(() => PlayChosen, MoneyGate.Open);

        Assert.True(closed.Allows(MoneyMode.Off));
        Assert.False(closed.Allows(MoneyMode.Play));
        Assert.True(open.Allows(MoneyMode.Play));
        Assert.False(new MoneyGate(() => PlayChosen, m => MoneyRelease.Effective(m, playApproved: true, realApproved: false)).Allows(MoneyMode.Real));
    }
}
