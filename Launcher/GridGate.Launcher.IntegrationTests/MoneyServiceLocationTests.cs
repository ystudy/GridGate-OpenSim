using System.Net;
using GridGate.Launcher.Core.Money;
using GridGate.Launcher.Core.Settings;
using Xunit;
using static GridGate.Launcher.IntegrationTests.MoneyServiceRig;

namespace GridGate.Launcher.IntegrationTests;

/// <summary>
/// The payer check through the service (money design §8.5): coins leave an avatar only when the service's own record has
/// them verified in the calling key's region the request names, and that region is verified. Where they are comes from
/// <c>/handoff</c> and <c>/arrived</c> (§8.4), never from Robust's Presence.
/// </summary>
[Collection(GridCollection.Name)]
public sealed class MoneyServiceLocationTests(MoneyMariaDb maria) : IClassFixture<MoneyMariaDb>, IAsyncDisposable
{
    private static readonly Guid C = Guid.Parse("d0000000-0000-0000-0000-00000000000d"); // a resident who isn't logged in

    private MoneyServiceRig? _rig;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_rig is not null) await _rig.DisposeAsync();
    }

    private async Task<MoneyServiceRig> StartAsync(bool place = true) => _rig = await MoneyServiceRig.StartAsync(maria, place);

    private static object Resident(Guid id) => new { agentId = id, home = "" };

    /// <summary>A request on <paramref name="path"/> that takes coins from <paramref name="payer"/> in <paramref name="region"/>.</summary>
    private static object Body(string path, object payer, Guid region, object? payee = null) => path switch
    {
        "transfer" => new { txnId = Guid.NewGuid(), type = "pay_avatar", from = payer, to = payee ?? Resident(B), amount = 10, regionId = region },
        "hold" => new { txnId = Guid.NewGuid(), kind = "object", buyer = payer, seller = payee ?? Resident(B), amount = 10, regionId = region, objectId = Guid.NewGuid(), objectName = "dress" },
        "charge" => new { txnId = Guid.NewGuid(), type = "upload_fee", payer, amount = 3, regionId = region },
        _ => throw new ArgumentException(path),
    };

    private static object Payout(Guid region) =>
        new { txnId = Guid.NewGuid(), ownerId = A, to = Resident(B), amount = 5, regionId = region, objectId = Guid.NewGuid(), objectName = "slot" };

    private static Task<long> Moved(MoneyServiceRig rig) =>
        rig.Db.Scalar("SELECT (SELECT COUNT(*) FROM `money`.`entries` WHERE type <> 'starting_balance') + (SELECT COUNT(*) FROM `money`.`holds`)");

    /// <summary>Asserts <paramref name="body"/> on <paramref name="path"/> with <paramref name="key"/> is refused with <paramref name="code"/> and moves nothing.</summary>
    private static async Task AssertRefused(MoneyServiceRig rig, string path, object body, string code, string key = Key)
    {
        var before = await Moved(rig);
        var answer = await rig.Post(path, body, key);
        Assert.Equal(HttpStatusCode.OK, answer.Status);
        Assert.True(CodeOf(answer) == code, $"{path} answered {answer.Raw}, not {code}");
        Assert.Equal(before, await Moved(rig));
        await rig.AssertBooksSoundAsync();
    }

    /// <summary>A logs in at Here before p1's module has checked in: placed, but nobody vouched for Here, so unverified.</summary>
    private async Task<MoneyServiceRig> UnverifiedAtHere()
    {
        var rig = await StartAsync(place: false);
        Assert.Equal("unverified_avatar", await rig.Place(SessionA, Here));
        await rig.Health(Key);
        await rig.Health(Key2);
        return rig;
    }

    /// <summary>Everyone placed, then p1's module goes quiet for longer than <see cref="RegionVerification.HeardWithin"/>.</summary>
    private async Task<MoneyServiceRig> HereGoneQuiet()
    {
        var rig = await StartAsync();
        rig.VerifyClock.Advance(RegionVerification.HeardWithin + TimeSpan.FromMinutes(1));
        return rig;
    }

    /// <summary>
    /// A wrong region two ways: p2 asks for A, who is at p1's Here; and p1 names There, which isn't where A is (nor p1's).
    /// </summary>
    private async Task AssertWrongRegion(string path)
    {
        var rig = await StartAsync();
        await AssertRefused(rig, path, Body(path, WhoA(), There), "wrong_region", Key2);
        await AssertRefused(rig, path, Body(path, WhoA(), There), "wrong_region");
    }

    [Fact]
    public async Task Every_payment_works_for_a_verified_avatar()
    {
        // The refusals below mean something only if the same requests succeed when nothing is wrong.
        var rig = await StartAsync();
        await rig.Ledger.SetPayoutsAllowedAsync(A, true, Ct);

        foreach (var path in new[] { "transfer", "hold", "charge" })
            Assert.True(CodeOf(await rig.Post(path, Body(path, WhoA(), Here))) == "ok", path);
        Assert.Equal("ok", CodeOf(await rig.Post("payout", Payout(Here))));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task Transfer_refuses_unverified_avatar() =>
        await AssertRefused(await UnverifiedAtHere(), "transfer", Body("transfer", WhoA(), Here), "unverified_avatar");

    [Fact]
    public async Task Transfer_refuses_unverified_region() =>
        await AssertRefused(await HereGoneQuiet(), "transfer", Body("transfer", WhoA(), Here), "unverified_region");

    [Fact]
    public Task Transfer_refuses_wrong_region() => AssertWrongRegion("transfer");

    [Fact]
    public async Task Hold_refuses_unverified_avatar() =>
        await AssertRefused(await UnverifiedAtHere(), "hold", Body("hold", WhoA(), Here), "unverified_avatar");

    [Fact]
    public async Task Hold_refuses_unverified_region() =>
        await AssertRefused(await HereGoneQuiet(), "hold", Body("hold", WhoA(), Here), "unverified_region");

    [Fact]
    public Task Hold_refuses_wrong_region() => AssertWrongRegion("hold");

    [Fact]
    public async Task Charge_refuses_unverified_avatar() =>
        await AssertRefused(await UnverifiedAtHere(), "charge", Body("charge", WhoA(), Here), "unverified_avatar");

    [Fact]
    public async Task Charge_refuses_unverified_region() =>
        await AssertRefused(await HereGoneQuiet(), "charge", Body("charge", WhoA(), Here), "unverified_region");

    [Fact]
    public Task Charge_refuses_wrong_region() => AssertWrongRegion("charge");

    [Fact]
    public async Task Charge_without_a_region_is_bad_request()
    {
        var rig = await StartAsync();

        var answer = await rig.Post("charge", new { txnId = Guid.NewGuid(), type = "upload_fee", payer = WhoA(), amount = 3 });

        Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
    }

    [Fact]
    public async Task Payout_refuses_unverified_region()
    {
        var rig = await HereGoneQuiet();
        await rig.Ledger.SetPayoutsAllowedAsync(A, true, Ct);

        await AssertRefused(rig, "payout", Payout(Here), "unverified_region");
    }

    [Fact]
    public async Task Payout_refuses_wrong_region()
    {
        var rig = await StartAsync();
        await rig.Ledger.SetPayoutsAllowedAsync(A, true, Ct);

        await AssertRefused(rig, "payout", Payout(There), "wrong_region");
    }

    [Fact]
    public async Task Neon_Pier_story_cannot_charge_someone_it_saw_next_door()
    {
        // p2's There saw A next door in Here and claims A arrived, to charge them (money design §8.4, the Neon Pier attack).
        var rig = await StartAsync();
        var balance = await rig.Db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{A}'");

        var claim = await rig.Arrived(Key2, SessionA, A, There);
        Assert.Equal("unverified_avatar", CodeOf(claim));

        var answer = await rig.Post("transfer", Body("transfer", WhoA(), There), Key2);
        Assert.True(CodeOf(answer) is "unverified_avatar" or "wrong_region", answer.Raw);
        // Two keys disagree about A, so neither may charge them now.
        Assert.Equal("unverified_avatar", CodeOf(await rig.Post("transfer", Body("transfer", WhoA(), Here))));
        Assert.Equal(balance, await rig.Db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{A}'"));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task Offline_vendor_owner_can_be_paid()
    {
        // Receivers need no place (money design §8.5): a vendor's owner is usually offline.
        var rig = await StartAsync();
        await rig.Db.AddUser(C);

        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Body("transfer", WhoA(), Here, Resident(C)))));
        var hold = await rig.Post("hold", Body("hold", WhoA(), Here, Resident(C)));
        Assert.Equal("ok", CodeOf(hold));
        Assert.Equal("ok", CodeOf(await rig.Post("hold/complete", new { txnId = Guid.NewGuid(), holdId = hold.Body!["holdId"]!.GetValue<long>() })));
        Assert.Equal(120, await rig.Db.Scalar($"SELECT balance FROM `money`.`accounts` WHERE agent_id = '{C}'"));
        await rig.AssertBooksSoundAsync();
    }

    [Fact]
    public async Task A_payer_who_moved_on_pays_only_where_they_are()
    {
        var rig = await StartAsync();

        await rig.MoveToThere(SessionA);

        await AssertRefused(rig, "transfer", Body("transfer", WhoA(), Here), "wrong_region");
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Body("transfer", WhoA(), There), Key2)));
    }

    [Fact]
    public async Task Handoff_and_arrival_work_while_money_is_off()
    {
        var rig = await StartAsync();
        rig.Mode = MoneyMode.Off;

        Assert.Equal("unavailable", CodeOf(await rig.Post("transfer", Body("transfer", WhoA(), Here))));
        // A teleport to another grid that fails, then a teleport that works: where A is still follows them while coins are
        // off.
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, There, "other_grid"));
        Assert.Equal("ok", CodeOf(await rig.Post("handoff/cancel", new { sessionId = SessionA, fromRegion = Here })));
        await rig.MoveToThere(SessionA);

        rig.Mode = MoneyMode.Play;
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Body("transfer", WhoA(), There), Key2)));
    }

    [Fact]
    public async Task Handoff_answers()
    {
        var rig = await StartAsync();

        Assert.Equal("wrong_region", await rig.Handoff(Key, SessionA, There, Here)); // There isn't p1's
        Assert.Equal("not_held", await rig.Handoff(Key, SessionB, Here, There)); // B isn't at Here
        Assert.Equal("ok", await rig.Handoff(Key, SessionA, Here, There, "crossing"));
        Assert.Equal("ok", await rig.Handoff(Key, SessionV, Here, There, "other_grid"));
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Post("handoff", new { id = Guid.NewGuid(), sessionId = SessionA, fromRegion = Here, toRegion = There, kind = "walk" })).Status);
    }

    [Fact]
    public async Task Arrival_answers()
    {
        var rig = await StartAsync(place: false);

        Assert.Equal("no_session", CodeOf(await rig.Arrived(Key, Guid.NewGuid(), A, Here)));
        Assert.Equal("wrong_region", CodeOf(await rig.Arrived(Key, SessionA, A, There)));
        await rig.Health(Key);
        await rig.Db.AddSessionStart(SessionA, A, Here, "login");
        Assert.Equal("ok", CodeOf(await rig.Arrived(Key, SessionA, A, Here)));
        Assert.Equal("ok", CodeOf(await rig.Arrived(Key, SessionA, A, Here))); // reported again: the same place
        // p2 hasn't checked in, so There can't knock A out of Here: refused, and A still pays at Here.
        Assert.Equal("refused", CodeOf(await rig.Arrived(Key2, SessionA, A, There)));
        Assert.Equal("ok", CodeOf(await rig.Post("transfer", Body("transfer", WhoA(), Here))));
    }

    [Fact]
    public async Task Arrived_body_with_unknown_field_is_bad_request()
    {
        var rig = await StartAsync();

        var answer = await rig.Post("arrived", new { id = Guid.NewGuid(), sessionId = SessionA, agentId = A, regionId = Here, presence = Here });

        Assert.Equal(HttpStatusCode.BadRequest, answer.Status);
        Assert.Equal("bad_request", CodeOf(answer));
    }

    [Fact]
    public async Task Balance_needs_a_place_in_the_keys_regions_in_any_state()
    {
        var rig = await UnverifiedAtHere();

        Assert.Equal("ok", CodeOf(await rig.Post("balance", WhoA()))); // unverified: can't pay, can see
        Assert.Equal("wrong_region", CodeOf(await rig.Post("balance", WhoA(), Key2)));
        Assert.Equal("wrong_region", CodeOf(await rig.Post("balance", WhoB()))); // logged in, never placed
        Assert.Equal("no_session", CodeOf(await rig.Post("balance", WhoA(secure: Guid.NewGuid()))));
    }

    [Fact]
    public async Task Arrived_spam_beyond_20_waiting_is_refused_at_once()
    {
        // A key spamming arrivals nothing vouches for must not hold the database's connections: 20 may wait for their
        // handoff, the rest are refused before any lookup. The location clock stands still, so the 20 stay held.
        var rig = await StartAsync(place: false);
        // 25 people logged in, each with an arrival of their own: 20 are held, 5 are refused at once.
        var people = Enumerable.Range(0, 25).Select(_ => (Agent: Guid.NewGuid(), Session: Guid.NewGuid())).ToList();
        foreach (var (agent, session) in people) await rig.Db.AddPresence(agent, Here, session, Guid.NewGuid());
        var calls = people.Select(p => rig.Post("arrived", new { id = Guid.NewGuid(), sessionId = p.Session, agentId = p.Agent, regionId = Here })).ToList();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (calls.Count(c => c.IsCompleted) < 5)
        {
            Assert.True(DateTime.UtcNow < deadline, "the arrivals past 20 were never refused");
            await Task.WhenAny(Task.WhenAll(calls), Task.Delay(20, Ct));
        }
        var refused = calls.Where(c => c.IsCompleted).ToList();
        foreach (var r in refused) Assert.Equal("refused", CodeOf(await r));
        var waiting = calls.Except(refused).ToList();
        Assert.Equal(20, waiting.Count);

        // A probe for a session nobody has: looked up, it would be no_session. Refused means nothing was looked up.
        object Probe() => new { id = Guid.NewGuid(), sessionId = Guid.NewGuid(), agentId = A, regionId = Here };
        Assert.Equal("refused", CodeOf(await rig.Post("arrived", Probe())));
        Assert.Equal("no_session", CodeOf(await rig.Post("arrived", Probe(), Key2))); // another key's own count
        Assert.All(waiting, w => Assert.False(w.IsCompleted));

        while (waiting.Any(w => !w.IsCompleted))
        {
            Assert.True(DateTime.UtcNow < deadline.AddSeconds(30), "the held arrivals never answered");
            rig.LocationClock.Advance(MoneyLocations.ArrivalRetry);
            await Task.WhenAny(Task.WhenAll(waiting), Task.Delay(20, Ct));
        }
        foreach (var w in waiting) Assert.Equal("unverified_avatar", CodeOf(await w));
        Assert.Equal("no_session", CodeOf(await rig.Post("arrived", Probe()))); // every held place given back
    }
}
