using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;

namespace GridGate.Launcher.Tests;

/// <summary>The one place code reads the effective money mode (money plan 2, task 1).</summary>
public sealed class MoneyGateTests
{
    private static readonly LauncherSettings PlayChosen = TestSettings.Create() with { Money = new MoneySettings(Mode: MoneyMode.Play) };

    [Fact]
    public void Gate_reads_off_until_approved()
    {
        var gate = new MoneyGate(() => PlayChosen);

        Assert.Equal(MoneyMode.Off, gate.Effective());
        Assert.False(gate.On);
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
        var closed = new MoneyGate(() => PlayChosen);
        var open = new MoneyGate(() => PlayChosen, MoneyGate.Open);

        Assert.True(closed.Allows(MoneyMode.Off));
        Assert.False(closed.Allows(MoneyMode.Play));
        Assert.True(open.Allows(MoneyMode.Play));
        Assert.False(new MoneyGate(() => PlayChosen, m => MoneyRelease.Effective(m, playApproved: true, realApproved: false)).Allows(MoneyMode.Real));
    }
}
