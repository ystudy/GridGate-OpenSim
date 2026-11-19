using GridGate.Region.Money;
using Xunit;

namespace GridGate.Money.Tests;

/// <summary>Visitor and unpaired Region Server answers in the region module (money plan 3, task 2).</summary>
public sealed class MoneyActionsVisitorTests
{
    private static readonly Guid Me = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid Them = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid Region = Guid.Parse("e0000000-0000-0000-0000-00000000000e");
    private static readonly Guid PartId = Guid.Parse("d0000000-0000-0000-0000-00000000000d");
    private static readonly Payer Visitor = new(Me, "http://other.example:8002/", Guid.NewGuid(), Guid.NewGuid());

    private readonly FakeView _view = new();
    private readonly EconomyInfo _economy = new("C$", 0, 0, true, true, 1_000_000);

    private static PartInfo Part() =>
        new(PartId, PartId, true, Them, Guid.Empty, "dress", [-2, -2, -2, -2, -2], 1, 10, true);

    [Fact]
    public void Visitors_off_has_its_own_text() =>
        Assert.Equal(MoneyRules.VisitorsOffText, MoneyRules.Message("visitors_off"));

    [Fact]
    public void Not_paired_has_its_own_text() =>
        Assert.Equal("This region isn't connected to the grid's money yet.", MoneyRules.Message("not_paired"));

    [Fact]
    public void Account_frozen_is_not_the_grid_freeze()
    {
        Assert.NotEqual(MoneyRules.Message("frozen"), MoneyRules.Message("account_frozen"));
        Assert.Equal("Your coins are frozen. Please contact the grid's staff.", MoneyRules.Message("account_frozen"));
    }

    [Fact]
    public void Script_reasons_for_the_new_codes()
    {
        Assert.Equal("LINDENDOLLAR_ENTITYDOESNOTEXIST", MoneyRules.ScriptReason("visitors_off"));
        Assert.Equal("SERVICE_ERROR", MoneyRules.ScriptReason("not_paired"));
    }

    [Fact]
    public void New_messages_fit_250_chars()
    {
        foreach (var c in new[] { "visitors_off", "not_paired", "account_frozen" })
            Assert.InRange(MoneyRules.Message(c).Length, 1, 250);
    }

    [Fact]
    public async Task Visitor_payer_with_wallets_off_is_told_so()
    {
        var service = new FakeMoneyService();
        service.Queue("transfer", new MoneyAnswer("visitors_off", null, null, null, null));
        var actions = new MoneyActions(service, Region, () => _economy, _view);

        await actions.PayAvatarAsync(Visitor, Me, new MoneyWho(Them, ""), 5);

        Assert.Equal([(Me, MoneyRules.VisitorsOffText)], _view.Told);
        Assert.Empty(_view.Pushed);
    }

    [Fact]
    public void An_unpaired_server_tells_an_uploader_it_isnt_paired_not_to_try_again_in_a_minute()
    {
        var actions = new MoneyActions(new UnpairedMoneyService(), Region, () => null, _view);
        var resident = new Payer(Me, "", Guid.NewGuid(), Guid.NewGuid());

        Assert.False(actions.UploadCovered(resident, 10));

        var told = Assert.Single(_view.Told);
        Assert.Equal(MoneyRules.NotPairedUploadText, told.Text);
        Assert.Contains("isn't paired with the money service yet", told.Text);
        Assert.DoesNotContain("minute", told.Text);
    }

    [Fact]
    public async Task Unpaired_service_refuses_every_action()
    {
        var service = new UnpairedMoneyService();
        var actions = new MoneyActions(service, Region, () => _economy, _view);
        var resident = new Payer(Me, "", Guid.NewGuid(), Guid.NewGuid());
        var delivered = false;

        await actions.PayAvatarAsync(resident, Me, new MoneyWho(Them, ""), 5);
        var bought = await actions.BuyAsync(resident, Part(), new MoneyWho(Them, ""), 1, 10, () => delivered = true);
        var paid = actions.Payout(Me, new MoneyWho(Them, ""), 5, PartId, "jar", Guid.Empty, out var reason);
        await actions.PushEntryBalanceAsync(resident);

        Assert.All(_view.Told, t => Assert.Equal(MoneyRules.Message("not_paired"), t.Text));
        Assert.Equal(2, _view.Told.Count);
        Assert.False(bought);
        Assert.False(delivered);
        Assert.False(paid);
        Assert.Equal("SERVICE_ERROR", reason);
        Assert.Empty(_view.Pushed);
        Assert.Null(await service.EconomyAsync(CancellationToken.None));
    }
}
