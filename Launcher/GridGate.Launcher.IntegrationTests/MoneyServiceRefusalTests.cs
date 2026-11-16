using System.Net;
using GridGate.Launcher.Core.Money;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// Refusals money design §12 asks for, through the service, on every endpoint that moves coins for an avatar (money review
/// 2026-11-15, L-8). They were tested on <c>/balance</c> only: an avatar in another program's region, a stale session, a
/// forged one, and a visitor's forged session or look-alike home. Each refusal moves nothing and leaves the books sound.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyServiceRefusalTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private MoneyServiceRig? _rig;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null) await _rig.DisposeAsync();
    }

    private async Task<MoneyServiceRig> StartAsync() => _rig = await MoneyServiceRig.StartAsync(maria);

    /// <summary>One request per endpoint that moves coins for <paramref name="payer"/>, all in region Here (key p1's).</summary>
    private static IEnumerable<(string Path, object Body)> Moves(object payer, object payee) =>
    [
        ("transfer", new { txnId = Guid.NewGuid(), type = "pay_avatar", from = payer, to = payee, amount = 10, regionId = Here }),
        ("hold", new { txnId = Guid.NewGuid(), kind = "object", buyer = payer, seller = payee, amount = 10, regionId = Here, objectId = Guid.NewGuid(), objectName = "dress" }),
        ("charge", new { txnId = Guid.NewGuid(), type = "upload_fee", payer, amount = 3 }),
    ];

    private static object Resident(Guid id) => new { agentId = id, home = "" };

    private static Task<long> Moved(MoneyServiceRig rig) =>
        rig.Db.Scalar("SELECT (SELECT COUNT(*) FROM `money`.`entries` WHERE type <> 'starting_balance') + (SELECT COUNT(*) FROM `money`.`holds`)");

    private async Task AssertRefusedEverywhere(MoneyServiceRig rig, object payer, string code)
    {
        var before = await Moved(rig);
        foreach (var (path, body) in Moves(payer, Resident(B)))
        {
            var answer = await rig.Post(path, body);
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            Assert.True(CodeOf(answer) == code, $"{path} answered {answer.Raw}, not {code}");
        }
        Assert.Equal(before, await Moved(rig));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task Every_move_works_for_a_present_avatar()
    {
        // The refusals below mean something only if the same requests succeed when nothing is wrong.
        var rig = await StartAsync();

        foreach (var (path, body) in Moves(WhoA(), Resident(B)))
            Assert.True(CodeOf(await rig.Post(path, body)) == "ok", path);
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task An_avatar_in_another_programs_region_is_refused_on_every_move()
    {
        // B is logged in at There, which key p1 doesn't cover.
        var rig = await StartAsync();

        await AssertRefusedEverywhere(rig, WhoB(), "wrong_region");
    }

    [Fact]
    public async Task An_avatar_who_moved_to_another_programs_region_is_refused_on_every_move()
    {
        var rig = await StartAsync();
        await rig.Db.Sql($"UPDATE `robust`.`Presence` SET RegionID = '{There}' WHERE UserID = '{A}'");

        await AssertRefusedEverywhere(rig, WhoA(), "wrong_region");
    }

    [Fact]
    public async Task A_stale_session_is_refused_on_every_move()
    {
        var rig = await StartAsync();
        await rig.Db.Sql($"DELETE FROM `robust`.`Presence` WHERE UserID = '{A}'");

        await AssertRefusedEverywhere(rig, WhoA(), "no_session");
    }

    [Fact]
    public async Task A_forged_session_is_refused_on_every_move()
    {
        var rig = await StartAsync();

        await AssertRefusedEverywhere(rig, WhoA(session: Guid.NewGuid()), "no_session");
        await AssertRefusedEverywhere(rig, WhoA(secure: Guid.NewGuid()), "no_session");
    }

    [Fact]
    public async Task A_visitors_forged_session_is_refused_on_every_move()
    {
        var rig = await StartAsync();

        Assert.Equal("no_session", CodeOf(await rig.Post("balance", WhoV(secure: Guid.NewGuid()))));
        await AssertRefusedEverywhere(rig, WhoV(secure: Guid.NewGuid()), "no_session");
    }

    [Fact]
    public async Task A_present_visitor_can_pay()
    {
        var rig = await StartAsync();
        await rig.Ledger.GiveAsync(new AccountRef(V, VisitorHome), 50, new StaffAction("Owner Person", "welcome"), TestContext.Current.CancellationToken);

        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Moves(WhoV(), Resident(B)).First().Body)));
        await rig.AssertBooksSoundAsync();
    }

    [Theory]
    [InlineData("http://othér.example:8002")]
    [InlineData("http://OTHÉR.example:8002")]
    [InlineData("http://other.example:8003")]
    [InlineData("https://other.example:8002")]
    public async Task A_visitors_real_session_with_a_look_alike_home_never_reaches_their_wallet(string lookAlike)
    {
        // L-2 through the service: Presence knows only the UUID, so the home is the ledger's to check.
        var rig = await StartAsync();
        await rig.Ledger.GiveAsync(new AccountRef(V, VisitorHome), 50, new StaffAction("Owner Person", "welcome"), TestContext.Current.CancellationToken);

        var balance = await rig.Post("balance", WhoV(lookAlike));
        Assert.Equal("no_wallet", CodeOf(balance));
        await AssertRefusedEverywhere(rig, WhoV(lookAlike), "no_wallet");
        Assert.Equal(50, await rig.Db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{V}'"));
        Assert.Equal(1, await rig.Db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE agent_id = '{V}'"));
    }

    [Fact]
    public async Task Paying_a_visitor_at_a_look_alike_home_opens_no_wallet()
    {
        var rig = await StartAsync();

        var answer = await rig.Post("transfer", Moves(WhoA(), new { agentId = V, home = "http://othér.example:8002" }).First().Body);

        Assert.Equal("no_wallet", CodeOf(answer));
        Assert.Equal(0, await rig.Db.Scalar($"SELECT COUNT(*) FROM `money`.`accounts` WHERE agent_id = '{V}'"));
        await rig.AssertBooksSoundAsync();
    }
}
