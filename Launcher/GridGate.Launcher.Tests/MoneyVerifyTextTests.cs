using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using GridGate.Launcher.Money;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class MoneyVerifyTextTests
{
    [Fact]
    public void Two_servers_at_one_address_are_told_apart_by_their_first_port_in_a_waiting_row()
    {
        var first = new PairedServer(Guid.NewGuid(), "10.0.0.5", new string('A', 64), DateTimeOffset.UtcNow, 9000);
        var second = new PairedServer(Guid.NewGuid(), "10.0.0.5", new string('B', 64), DateTimeOffset.UtcNow, 9100);

        var a = MoneyVerifyText.Words(RegionStatus.Waiting, MoneyVerifyText.ServerName(first));
        var b = MoneyVerifyText.Words(RegionStatus.Waiting, MoneyVerifyText.ServerName(second));

        Assert.Contains("10.0.0.5", a);
        Assert.Contains("ports from 9000", a);
        Assert.Contains("ports from 9100", b);
    }
}
