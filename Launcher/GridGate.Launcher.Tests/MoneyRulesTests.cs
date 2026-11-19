using GridGate.Launcher.Core.Money;
using Xunit;

namespace GridGate.Launcher.Tests;

public sealed class MoneyRulesTests
{
    private static readonly Guid Before = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid After = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static RegionKeyGrant ServerKey(Guid id) => new("server:" + id.ToString("N"), new HashSet<Guid>(), "192.168.1.20");

    private static MoneySettings Paired(string? machine) => new(PairedServers:
    [
        new PairedServer(Before, "192.168.1.20", new string('A', 64), DateTimeOffset.UtcNow, 9000, machine),
        new PairedServer(After, "192.168.1.20", new string('B', 64), DateTimeOffset.UtcNow, 9000, machine),
    ]);

    [Fact]
    public void A_server_that_gave_a_computer_id_keeps_its_scope_across_a_new_pairing()
    {
        var money = Paired("machine-1");

        Assert.Equal(MoneyRules.TxnScope(money, ServerKey(Before)), MoneyRules.TxnScope(money, ServerKey(After)));
    }

    [Fact]
    public void Two_computers_never_share_a_scope()
    {
        var money = new MoneySettings(PairedServers:
        [
            new PairedServer(Before, "192.168.1.20", new string('A', 64), DateTimeOffset.UtcNow, 9000, "machine-1"),
            new PairedServer(After, "192.168.1.21", new string('B', 64), DateTimeOffset.UtcNow, 9000, "machine-2"),
        ]);

        Assert.NotEqual(MoneyRules.TxnScope(money, ServerKey(Before)), MoneyRules.TxnScope(money, ServerKey(After)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_server_without_a_computer_id_is_scoped_by_its_key(string? machine)
    {
        var money = Paired(machine);

        Assert.Equal("server:" + Before.ToString("N"), MoneyRules.TxnScope(money, ServerKey(Before)));
        Assert.NotEqual(MoneyRules.TxnScope(money, ServerKey(Before)), MoneyRules.TxnScope(money, ServerKey(After)));
    }

    [Fact]
    public void A_program_key_is_scoped_by_its_key_whatever_servers_are_paired()
    {
        Assert.Equal("p1", MoneyRules.TxnScope(Paired("machine-1"), new RegionKeyGrant("p1", new HashSet<Guid>())));
    }

    [Theory]
    [InlineData(EntryType.ClassifiedFee, 25, 5, 25, true)]
    [InlineData(EntryType.ClassifiedFee, 50, 5, 25, false)]
    [InlineData(EntryType.UploadFee, 10, 5, 25, true)]
    [InlineData(EntryType.UploadFee, 7, 5, 25, false)]
    [InlineData(EntryType.UploadFee, 5, 0, 25, false)]
    [InlineData(EntryType.GroupJoinFee, 7, 5, 25, true)]
    public void Fee_amounts_follow_the_configured_fees(string type, long amount, long upload, long classified, bool ok) =>
        Assert.Equal(ok, MoneyRules.FeeAmountOk(type, amount, new MoneySettings(UploadFee: upload, ClassifiedFee: classified)));
}
