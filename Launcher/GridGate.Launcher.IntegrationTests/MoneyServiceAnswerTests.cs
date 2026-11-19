using System.Net;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// What the money service answers: who learns a payee's balance, whose session a key may read, a repeat that gets its first
/// answer whatever changed since, who may settle a hold, and the refusals that are counted.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyServiceAnswerTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private static readonly Guid C = Guid.Parse("d0000000-0000-0000-0000-00000000000d");
    private static readonly Guid SessionC = Guid.Parse("5000000d-0000-0000-0000-000000000005");
    private static readonly Guid SecureC = Guid.Parse("6000000d-0000-0000-0000-000000000006");

    private MoneyServiceRig? _rig;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null) await _rig.DisposeAsync();
    }

    private async Task<MoneyServiceRig> StartAsync(bool place = true) => _rig = await MoneyServiceRig.StartAsync(maria, place);

    private static object Resident(Guid id) => new { agentId = id, home = "" };

    private static object PayAvatar(Guid txn, object from, object to, long amount = 10, Guid? region = null) =>
        new { txnId = txn, type = "pay_avatar", from, to, amount, regionId = region ?? Here };

    private static object HoldBody(Guid txn, long amount = 25) => new
    {
        txnId = txn, kind = "object", buyer = WhoA(), seller = Resident(B), amount, regionId = Here, objectId = Guid.NewGuid(), objectName = "dress",
    };

    // A paying region learns the payee's balance only where the payee is.

    [Fact]
    public async Task A_transfer_shows_the_payees_balance_only_when_they_are_in_a_region_the_key_covers()
    {
        var rig = await StartAsync();

        // B is placed at There, which p1's key doesn't cover: A's region is told nothing of B's balance.
        var toB = await rig.Post("transfer", PayAvatar(Guid.NewGuid(), WhoA(), Resident(B)));
        Assert.Equal("ok", CodeOf(toB));
        Assert.Equal(90, toB.Body!["balance"]!.GetValue<long>());
        Assert.Null(toB.Body["toBalance"]);

        // V, a visitor, is verified at Here: p1 may see their balance.
        var toV = await rig.Post("transfer", PayAvatar(Guid.NewGuid(), WhoA(), new { agentId = V, home = VisitorHome }));
        Assert.Equal("ok", CodeOf(toV));
        Assert.NotNull(toV.Body!["toBalance"]);

        // Once B is at Here too, p1 sees B's balance (110 after the first payment, 5 more now).
        await rig.Handoff(Key2, SessionB, There, Here);
        Assert.Equal("ok", CodeOf(await rig.Arrived(Key, SessionB, B, Here)));
        var again = await rig.Post("transfer", PayAvatar(Guid.NewGuid(), WhoA(), Resident(B), 5));
        Assert.Equal(115, again.Body!["toBalance"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_transfer_to_someone_nobody_placed_shows_no_balance()
    {
        var rig = await StartAsync();
        await rig.Db.AddUser(C);

        var answer = await rig.Post("transfer", PayAvatar(Guid.NewGuid(), WhoA(), Resident(C)));

        Assert.Equal("ok", CodeOf(answer));
        Assert.Null(answer.Body!["toBalance"]);
    }

    // A key reads the balance of anyone whose session it learned by a match, no one else's.

    private async Task<MoneyServiceRig> StartWithUnplacedCAsync()
    {
        var rig = await StartAsync(place: false);
        await rig.Health(Key);
        await rig.Health(Key2);
        await rig.Db.AddUser(C);
        await rig.Db.AddPresence(C, There, SessionC, SecureC);
        return rig;
    }

    private static object WhoC() => new { agentId = C, home = "", sessionId = SessionC, secureSessionId = SecureC };

    [Fact]
    public async Task A_claim_nothing_vouches_for_doesnt_let_a_key_read_the_balance()
    {
        var rig = await StartWithUnplacedCAsync();

        // p1 claims C arrived at Here with no start and no handoff: placed unverified, money off for C.
        Assert.Equal("unverified_avatar", CodeOf(await rig.Arrived(Key, SessionC, C, Here)));

        Assert.Equal("wrong_region", CodeOf(await rig.Post("balance", WhoC())));
    }

    [Fact]
    public async Task A_session_a_key_placed_by_a_match_reads_its_balance_even_while_unverified()
    {
        var rig = await StartAsync(place: false);
        // Only p1 has checked in: There isn't verified, but C's login start there is a match.
        await rig.Health(Key);
        await rig.Db.AddUser(C);
        await rig.Db.AddPresence(C, There, SessionC, SecureC);
        Assert.Equal("unverified_avatar", await rig.Place(SessionC, There, "login", Key2, C));

        var answer = await rig.Post("balance", WhoC(), Key2);

        Assert.Equal("ok", CodeOf(answer));
        Assert.Equal(100, answer.Body!["balance"]!.GetValue<long>());
        // Another key never reads it, even for a region it covers.
        Assert.Equal("wrong_region", CodeOf(await rig.Post("balance", WhoC(), Key)));
    }

    // A stored repeat gets its first answer whatever changed.

    [Fact]
    public async Task A_repeated_payment_gets_its_first_answer_after_coins_are_switched_off()
    {
        var rig = await StartAsync();
        var transfer = PayAvatar(Guid.NewGuid(), WhoA(), Resident(B));
        var charge = new { txnId = Guid.NewGuid(), type = "upload_fee", payer = WhoA(), amount = 3, regionId = Here };
        var hold = HoldBody(Guid.NewGuid());
        var first = new[] { await rig.Post("transfer", transfer), await rig.Post("charge", charge), await rig.Post("hold", hold) };
        Assert.All(first, a => Assert.Equal("ok", CodeOf(a)));

        rig.Mode = MoneyMode.Off;

        Assert.Equal(first[0].Raw, (await rig.Post("transfer", transfer)).Raw);
        Assert.Equal(first[1].Raw, (await rig.Post("charge", charge)).Raw);
        Assert.Equal(first[2].Raw, (await rig.Post("hold", hold)).Raw);
        // A different request with the same id isn't a repeat, and a new one gets nothing while coins are off.
        Assert.Equal("unavailable", CodeOf(await rig.Post("transfer", PayAvatar(Guid.NewGuid(), WhoA(), Resident(B)))));
        Assert.Equal("unavailable", CodeOf(await rig.Post("transfer", PayAvatar(((dynamic)transfer).txnId, WhoA(), Resident(B), 11))));
        // The repeats moved nothing: 10 + 3 + 25 left A's wallet once.
        Assert.Equal(62, await rig.Db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{A}' AND home = ''"));
    }

    [Fact]
    public async Task A_repeated_payment_gets_its_first_answer_after_the_key_stopped_covering_the_region()
    {
        var rig = await StartAsync();
        var transfer = PayAvatar(Guid.NewGuid(), WhoA(), Resident(B));
        var first = await rig.Post("transfer", transfer);
        Assert.Equal("ok", CodeOf(first));

        // Region Here now belongs to program p2.
        rig.Settings = rig.Settings with { Regions = [new RegionSettings("p2", "Here", Here, UdpPort: 8004), new RegionSettings("p2", "There", There, UdpPort: 8005)] };

        Assert.Equal(first.Raw, (await rig.Post("transfer", transfer)).Raw);
        Assert.Equal("wrong_region", CodeOf(await rig.Post("transfer", PayAvatar(Guid.NewGuid(), WhoA(), Resident(B)))));
    }

    [Fact]
    public async Task A_repeated_complete_gets_its_first_answer_after_the_key_stopped_covering_the_region()
    {
        var rig = await StartAsync();
        var holdId = (await rig.Post("hold", HoldBody(Guid.NewGuid()))).Body!["holdId"]!.GetValue<long>();
        var settle = new { txnId = Guid.NewGuid(), holdId };
        var first = await rig.Post("hold/complete", settle);
        Assert.Equal("ok", CodeOf(first));

        rig.Settings = rig.Settings with { Regions = [new RegionSettings("p2", "Here", Here, UdpPort: 8004), new RegionSettings("p2", "There", There, UdpPort: 8005)] };

        Assert.Equal(first.Raw, (await rig.Post("hold/complete", settle)).Raw);
    }

    // Only the key that made a hold settles it.

    [Theory]
    [InlineData("hold/complete")]
    [InlineData("hold/return")]
    public async Task Only_the_key_that_made_a_hold_settles_it(string path)
    {
        var rig = await StartAsync();
        var holdId = (await rig.Post("hold", HoldBody(Guid.NewGuid()))).Body!["holdId"]!.GetValue<long>();
        // Region Here moves to program p2, whose key now covers it.
        rig.Settings = rig.Settings with { Regions = [new RegionSettings("p2", "Here", Here, UdpPort: 8004), new RegionSettings("p2", "There", There, UdpPort: 8005)] };

        Assert.Equal("unknown_hold", CodeOf(await rig.Post(path, new { txnId = Guid.NewGuid(), holdId }, Key2)));

        Assert.Equal("open", await rig.Db.Text($"SELECT state FROM `money`.`holds` WHERE id = {holdId}"));
        // The sweep still returns a hold whose key is gone.
        await rig.Db.Sql($"UPDATE `money`.`holds` SET created = created - INTERVAL 1 HOUR WHERE id = {holdId}");
        Assert.Equal(1, await rig.Ledger.ReturnExpiredHoldsAsync(Ct));
        Assert.Equal("returned", await rig.Db.Text($"SELECT state FROM `money`.`holds` WHERE id = {holdId}"));
    }

    [Fact]
    public async Task The_key_that_made_a_hold_still_settles_it()
    {
        var rig = await StartAsync();
        var holdId = (await rig.Post("hold", HoldBody(Guid.NewGuid()))).Body!["holdId"]!.GetValue<long>();

        Assert.Equal("ok", CodeOf(await rig.Post("hold/return", new { txnId = Guid.NewGuid(), holdId })));
    }

    // Refused handoffs and uncovered arrivals are counted.

    [Fact]
    public async Task A_handoff_nobody_holds_is_counted_as_a_notice()
    {
        var rig = await StartAsync();

        // p1's key says SessionB left Here, but B is at There, placed by p2.
        Assert.Equal("not_held", await rig.Handoff(Key, SessionB, Here, There));

        var alert = Assert.Single(await rig.Alerts.OpenAsync(Ct));
        Assert.Equal(AlertLevel.Notice, alert.Level);
        Assert.Equal("p1", alert.KeyId);
    }

    [Fact]
    public async Task An_arrival_in_a_region_the_key_doesnt_cover_is_counted_as_a_notice()
    {
        var rig = await StartAsync();

        Assert.Equal("wrong_region", CodeOf(await rig.Arrived(Key, SessionA, A, There)));

        var alert = Assert.Single(await rig.Alerts.OpenAsync(Ct));
        Assert.Equal(AlertLevel.Notice, alert.Level);
        Assert.Equal("p1", alert.KeyId);
    }

    [Fact]
    public async Task An_honest_handoff_and_arrival_leave_no_notice()
    {
        var rig = await StartAsync();

        await rig.MoveToThere(SessionA);

        Assert.Empty(await rig.Alerts.OpenAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await rig.Post("balance", WhoA(), Key2)).Status);
    }
}
